// Copyright 2022 Jason Thorsness
namespace Jt.CanHasFreeSpace.Cli;

using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

public static class WindowsFileEnumerator
{
    public delegate void AcceptDelegate(
        ReadOnlyMemory<char> directory,
        ReadOnlySpan<char> fileName,
        ulong endOfFile,
        ulong allocationSize,
        ulong fileId);

    public delegate void EnumerationFailedDelegate(ReadOnlySpan<char> directory, int errorCode);

    private const int BufferLength = 16384;

    public static void Enumerate(
        string rootDirectory,
        AcceptDelegate acceptDelegate,
        EnumerationFailedDelegate enumerationFailedDelegate)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferLength);

        Stack<ReadOnlyMemory<char>> directories = new();
        directories.Push(rootDirectory.AsMemory());

        while (directories.TryPop(out ReadOnlyMemory<char> directory))
        {
            if (!Native.TryOpenDirectoryForRead(directory.Span, out SafeFileHandle safeFileHandle, out int error))
            {
                enumerationFailedDelegate(directory.Span, error);
                continue;
            }

            using (safeFileHandle)
            {
                while (Native.TryReadDirectoryEntries(safeFileHandle, buffer, out error))
                {
                    ReadOnlySpan<byte> bufferSpan = buffer;

                    while (Native.TryReadDirectoryEntryAndAdvance(
                               ref bufferSpan,
                               out ulong endOfFile,
                               out ulong allocationSize,
                               out uint fileAttributes,
                               out ReadOnlySpan<byte> fileId,
                               out ReadOnlySpan<char> fileName))
                    {
                        bool isSelfOrParent =
                            (fileName.Length == 1 && fileName[0] == '.') ||
                            (fileName.Length == 2 && fileName[0] == '.' && fileName[1] == '.');

                        if (isSelfOrParent)
                        {
                            continue;
                        }

                        if (BinaryPrimitives.ReadUInt64LittleEndian(fileId[sizeof(ulong)..]) == 0)
                        {
                            fileId = fileId[..sizeof(ulong)];
                        }
                        else
                        {
                            throw new NotSupportedException("128-bit FileId not yet supported (ReFS?)");
                        }

                        acceptDelegate(directory, fileName, endOfFile, allocationSize, BinaryPrimitives.ReadUInt64LittleEndian(fileId));

                        if ((fileAttributes & (uint)FileAttributes.Directory) != 0 &&
                            (fileAttributes & (uint)FileAttributes.ReparsePoint) == 0)
                        {
                            Memory<char> fullPath = new char[directory.Length + 1 + fileName.Length];
                            if (!Path.TryJoin(directory.Span, fileName, fullPath.Span, out int charsWritten))
                            {
                                throw new CodeBugException();
                            }

                            fullPath = fullPath[..charsWritten];
                            directories.Push(fullPath);
                        }
                    }
                }

                if (error != 0)
                {
                    enumerationFailedDelegate(directory.Span, error);
                }
            }
        }

        ArrayPool<byte>.Shared.Return(buffer);
    }
}
