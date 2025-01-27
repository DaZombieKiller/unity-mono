﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.IO.Enumeration
{
    public unsafe abstract partial class FileSystemEnumerator<TResult> : CriticalFinalizerObject, IEnumerator<TResult>
    {
        private const int StandardBufferSize = 4096;

        // We need to have enough room for at least a single entry. The filename alone can be 512 bytes, we'll ensure we have
        // a reasonable buffer for all of the other metadata as well.
        private const int MinimumBufferSize = 1024;

        private readonly string _originalRootDirectory;
        private readonly string _rootDirectory;
        private readonly EnumerationOptions _options;

        private readonly object _lock = new object();

        private Interop.NtDll.FILE_FULL_DIR_INFORMATION* _entry;
        private TResult _current;

        private IntPtr _buffer;
        private int _bufferLength;
        private IntPtr _directoryHandle;
        private string _currentPath;
        private bool _lastEntryFound;
        private Queue<(IntPtr Handle, string Path)> _pending;

        /// <summary>
        /// Encapsulates a find operation.
        /// </summary>
        /// <param name="directory">The directory to search in.</param>
        /// <param name="options">Enumeration options to use.</param>
        public FileSystemEnumerator(string directory, EnumerationOptions options = null)
        {
            _originalRootDirectory = directory ?? throw new ArgumentNullException(nameof(directory));
            _rootDirectory = PathInternal.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            _options = options ?? EnumerationOptions.Default;

            // We'll only suppress the media insertion prompt on the topmost directory as that is the
            // most likely scenario and we don't want to take the perf hit for large enumerations.
            // (We weren't consistent with how we handled this historically.)
            using (new DisableMediaInsertionPrompt())
            {
                // We need to initialize the directory handle up front to ensure
                // we immediately throw IO exceptions for missing directory/etc.
                _directoryHandle = CreateDirectoryHandle(_rootDirectory);
                if (_directoryHandle == IntPtr.Zero)
                    _lastEntryFound = true;
            }

            _currentPath = _rootDirectory;

            int requestedBufferSize = _options.BufferSize;
            _bufferLength = requestedBufferSize <= 0 ? StandardBufferSize
                : Math.Max(MinimumBufferSize, requestedBufferSize);

            try
            {
                // NtQueryDirectoryFile needs its buffer to be 64bit aligned to work
                // successfully with FileFullDirectoryInformation on ARM32. AllocHGlobal
                // will return pointers aligned as such, new byte[] does not.
                _buffer = Marshal.AllocHGlobal(_bufferLength);
            }
            catch
            {
                // Close the directory handle right away if we fail to allocate
                CloseDirectoryHandle();
                throw;
            }
        }

        private void CloseDirectoryHandle()
        {
            // As handles can be reused we want to be extra careful to close handles only once
            IntPtr handle = Interlocked.Exchange(ref _directoryHandle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                Interop.Kernel32.CloseHandle(handle);
        }

        /// <summary>
        /// Simple wrapper to allow creating a file handle for an existing directory.
        /// </summary>
        private IntPtr CreateDirectoryHandle(string path, bool ignoreNotFound = false)
        {
            // GetFileAttributesEx sometimes does not understand long form UNC paths
            // without adding a trailing backslash. So we check explicitly for such a
            // path, and add the required trail.
            if ((path.StartsWith(@"\?\") || path.StartsWith(@"\\?\"))
                && path.Contains(@"GLOBALROOT\Device\Harddisk"))
            {
                // 'Partition' length is 9 and can be followed by a number between '1' and '14'.
                int diff = path.Length - path.IndexOf("Partition");

                // Previous code get rid of any directory separator ('/')
                // This leaves only "PartitionX", "PartitionXX" or "PartitionX\" to check.
                if (diff <= 11 && path[path.Length - 1] != '\\')
                {
                    path += '\\';
                }
            }

            IntPtr handle = System.IO.FileSystem.UnityCreateFile_IntPtr(
                path,
                Interop.Kernel32.FileOperations.FILE_LIST_DIRECTORY,
                FileShare.ReadWrite | FileShare.Delete,
                FileMode.Open,
                Interop.Kernel32.FileOperations.FILE_FLAG_BACKUP_SEMANTICS);

            if (handle == IntPtr.Zero || handle == (IntPtr)(-1))
            {
                int error = Marshal.GetLastWin32Error();

                if (ContinueOnDirectoryError(error, ignoreNotFound))
                {
                    return IntPtr.Zero;
                }

                if (error == Interop.Errors.ERROR_FILE_NOT_FOUND)
                {
                    // Historically we throw directory not found rather than file not found
                    error = Interop.Errors.ERROR_PATH_NOT_FOUND;
                }

                throw Win32Marshal.GetExceptionForWin32Error(error, path);
            }

            return handle;
        }

        private bool ContinueOnDirectoryError(int error, bool ignoreNotFound)
        {
            // Directories can be removed (ERROR_FILE_NOT_FOUND) or replaced with a file of the same name (ERROR_DIRECTORY) while
            // we are enumerating. The only reasonable way to handle this is to simply move on. There is no such thing as a "true"
            // snapshot of filesystem state- our "snapshot" will consider the name non-existent in this rare case.

            return (ignoreNotFound && (error == Interop.Errors.ERROR_FILE_NOT_FOUND || error == Interop.Errors.ERROR_PATH_NOT_FOUND || error == Interop.Errors.ERROR_DIRECTORY))
                || (error == Interop.Errors.ERROR_ACCESS_DENIED && _options.IgnoreInaccessible)
                || ContinueOnError(error);
        }

        public bool MoveNext()
        {
            if (_lastEntryFound)
                return false;

            FileSystemEntry entry = default;

            lock (_lock)
            {
                if (_lastEntryFound)
                    return false;

                do
                {
                    FindNextEntry();
                    if (_lastEntryFound)
                        return false;

                    // Calling the constructor inside the try block would create a second instance on the stack.
                    FileSystemEntry.Initialize(ref entry, _entry, _currentPath, _rootDirectory, _originalRootDirectory);

                    // Skip specified attributes
                    if ((_entry->FileAttributes & _options.AttributesToSkip) != 0)
                        continue;

                    if ((_entry->FileAttributes & FileAttributes.Directory) != 0)
                    {
                        // Subdirectory found
                        if (!(_entry->FileName.Length > 2 || _entry->FileName[0] != '.' || (_entry->FileName.Length == 2 && _entry->FileName[1] != '.')))
                        {
                            // "." or "..", don't process unless the option is set
                            if (!_options.ReturnSpecialDirectories)
                                continue;
                        }
                        else if (_options.RecurseSubdirectories && ShouldRecurseIntoEntry(ref entry))
                        {
                            // Recursion is on and the directory was accepted, Queue it
                            string subDirectory = Path.Join(_currentPath, _entry->FileName);
#if UNITY_AOT
                            // We ship the same class libs for Win32 & WinRT so we need to make a runtime
                            // decision on which GetData implementation to call
                            //IntPtr subDirectoryHandle = CreateRelativeDirectoryHandle(_entry->FileName, subDirectory);
                            IntPtr subDirectoryHandle;
                            if (System.AppDomain.IsAppXModel())
                                subDirectoryHandle = CreateRelativeDirectoryHandleUWP(_entry->FileName, subDirectory);
                            else
                                subDirectoryHandle = CreateRelativeDirectoryHandle(_entry->FileName, subDirectory);
#endif
                            if (subDirectoryHandle != IntPtr.Zero)
                            {
                                try
                                {
                                    if (_pending == null)
                                        _pending = new Queue<(IntPtr, string)>();
                                    _pending.Enqueue((subDirectoryHandle, subDirectory));
                                }
                                catch
                                {
                                    // Couldn't queue the handle, close it and rethrow
                                    Interop.Kernel32.CloseHandle(subDirectoryHandle);
                                    throw;
                                }
                            }
                        }
                    }

                    if (ShouldIncludeEntry(ref entry))
                    {
                        _current = TransformEntry(ref entry);
                        return true;
                    }
                } while (true);
            }
        }

        private unsafe void FindNextEntry()
        {
            _entry = Interop.NtDll.FILE_FULL_DIR_INFORMATION.GetNextInfo(_entry);
            if (_entry != null)
                return;

#if UNITY_AOT
            // We ship the same class libs for Win32 & WinRT so we need to make a runtime
            // decision on which GetData implementation to call
            if (System.AppDomain.IsAppXModel())
            {
                if (GetDataUWP())
                    _entry = (Interop.NtDll.FILE_FULL_DIR_INFORMATION*)_buffer;
            }
            else
            {
#endif
            // We need more data
            if (GetData())
                _entry = (Interop.NtDll.FILE_FULL_DIR_INFORMATION*)_buffer;
#if UNITY_AOT
            }
#endif
        }

        private bool DequeueNextDirectory()
        {
            if (_pending == null || _pending.Count == 0)
                return false;

            (_directoryHandle, _currentPath) = _pending.Dequeue();
            return true;
        }

        private void InternalDispose(bool disposing)
        {
            // It is possible to fail to allocate the lock, but the finalizer will still run
            if (_lock != null)
            {
                lock (_lock)
                {
                    _lastEntryFound = true;

                    CloseDirectoryHandle();

                    if (_pending != null)
                    {
                        while (_pending.Count > 0)
                            Interop.Kernel32.CloseHandle(_pending.Dequeue().Handle);
                        _pending = null;
                    }

                    if (_buffer != default)
                    {
                        Marshal.FreeHGlobal(_buffer);
                    }

                    _buffer = default;
                }
            }

            Dispose(disposing);
        }
    }
}
