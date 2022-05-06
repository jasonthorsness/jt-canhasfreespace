// Copyright 2022 Jason Thorsness
namespace Jt.CanHasFreeSpace.Cli;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static unsafe class Native
{
    private const int ErrorNoMoreFiles = 0x00000012;

    public static bool TryOpenDirectoryForRead(ReadOnlySpan<char> path, out SafeFileHandle safeFileHandle, out int error)
    {
        const string win32Prefix = "\\\\?\\";
        Span<char> pathArgument = stackalloc char[win32Prefix.Length + path.Length + 1];
        win32Prefix.CopyTo(pathArgument[..win32Prefix.Length]);
        path.CopyTo(pathArgument[win32Prefix.Length..^1]);
        pathArgument[^1] = '\0';

        fixed (char* pathArgumentPtr = pathArgument)
        {
            safeFileHandle = new SafeFileHandle(
                External.CreateFileW(
                    pathArgumentPtr,
                    (uint)FileAccess.Read,
                    (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
                    securityAttributes: default,
                    (uint)FileMode.Open,
                    flagsAndAttributes: 0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
                    templateFile: default),
                ownsHandle: true);

            if (safeFileHandle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            error = 0;
            return true;
        }
    }

    public static bool TryReadFileId(SafeFileHandle safeFileHandle, out ulong volumeSerialNumber, out Guid fileId, out int error)
    {
        const int fileIdInfoClass = 18;

        Span<byte> buffer = stackalloc byte[sizeof(External.FileIdInfo)];

        fixed (byte* bufferPtr = buffer)
        {
            if (!External.GetFileInformationByHandleEx(
                    safeFileHandle.DangerousGetHandle(),
                    fileIdInfoClass,
                    bufferPtr,
                    (uint)buffer.Length))
            {
                volumeSerialNumber = default;
                fileId = default;
                error = Marshal.GetLastWin32Error();
                return false;
            }
        }

        External.FileIdInfo result = MemoryMarshal.Read<External.FileIdInfo>(buffer);
        volumeSerialNumber = result.VolumeSerialNumber;
        fileId = new Guid(new Span<byte>(result.FileId, External.FileIdInfo.FileIdLength));
        error = 0;
        return true;
    }

    public static bool TryReadDirectoryEntries(SafeFileHandle safeFileHandle, byte[] buffer, out int error)
    {
        const int fileIdExtdDirectoryInfo = 19;

        fixed (byte* pBuffer = buffer)
        {
            if (!External.GetFileInformationByHandleEx(
                    safeFileHandle.DangerousGetHandle(),
                    fileIdExtdDirectoryInfo,
                    pBuffer,
                    (uint)buffer.Length))
            {
                error = Marshal.GetLastWin32Error();

                if (error == ErrorNoMoreFiles)
                {
                    // Success (no more files)
                    error = 0;
                }

                return false;
            }

            error = 0;
            return true;
        }
    }

    public static bool TryReadDirectoryEntryAndAdvance(
        ref ReadOnlySpan<byte> bufferSpan,
        out ulong endOfFile,
        out ulong allocationSize,
        out uint fileAttributes,
        out ReadOnlySpan<byte> fileId,
        out ReadOnlySpan<char> fileName)
    {
        if (bufferSpan.Length == 0)
        {
            endOfFile = default;
            allocationSize = default;
            fileAttributes = default;
            fileId = default;
            fileName = default;
            return false;
        }

        // FILE_ID_EXTD_DIR_INFO
        // https://docs.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_extd_dir_info
        const int fileIdLength = 16;
        const int endOfFileOffset = 40;
        const int allocationSizeOffset = endOfFileOffset + sizeof(ulong);
        const int fileAttributesOffset = allocationSizeOffset + sizeof(ulong);
        const int fileNameLengthOffset = fileAttributesOffset + sizeof(uint);
        const int fileIdOffset = fileNameLengthOffset + sizeof(uint) + sizeof(uint) + sizeof(uint);
        const int fileNameOffset = fileIdOffset + fileIdLength;

        int nextOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bufferSpan);
        endOfFile = BinaryPrimitives.ReadUInt64LittleEndian(bufferSpan[endOfFileOffset..]);
        allocationSize = BinaryPrimitives.ReadUInt64LittleEndian(bufferSpan[allocationSizeOffset..]);
        fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(bufferSpan[fileAttributesOffset..]);
        fileId = bufferSpan[fileIdOffset..][..fileIdLength];
        int fileNameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bufferSpan[fileNameLengthOffset..]);
        fileName = MemoryMarshal.Cast<byte, char>(bufferSpan[fileNameOffset..][..fileNameLength]);

        bufferSpan = (nextOffset > 0)
            ? bufferSpan[nextOffset..]
            : ReadOnlySpan<byte>.Empty;

        return true;
    }

    private static class External
    {
        // CreateFileW
        // https://docs.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(
            char* path,
            uint access,
            uint share,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        // GetFileInformationByHandleEx
        // https://docs.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandleEx(
            IntPtr hFile,
            int infoClass,
            byte* pInfo,
            uint dwBufferSize);

        // FILE_ID_INFO
        // https://docs.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info
        [StructLayout(LayoutKind.Sequential)]
        public struct FileIdInfo
        {
            public const int FileIdLength = 16;

            public readonly ulong VolumeSerialNumber;
            public fixed byte FileId[FileIdLength];
        }
    }
}
