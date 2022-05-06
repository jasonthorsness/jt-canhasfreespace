// Copyright 2022 Jason Thorsness
namespace Jt.CanHasFreeSpace.Cli;

using System.Globalization;
using System.Text;
using MimeTypes;

public class CsvWriter
{
    private const int MaxLevels = 12;

    private readonly StringBuilder stringBuilder;

    private ReadOnlyMemory<char> lastDirectory;
    private ReadOnlyMemory<char> lastDirectorySerialized;

    public CsvWriter()
    {
        this.stringBuilder = new StringBuilder();
        this.lastDirectory = ReadOnlyMemory<char>.Empty;
        this.lastDirectorySerialized = ReadOnlyMemory<char>.Empty;
    }

    public void WriteHeader(StreamWriter streamWriter)
    {
        streamWriter.Write("fileId,endOfFile,allocationSize,extension,mimeType,mimeSubtype");

        for (int i = 0; i < MaxLevels; i++)
        {
            streamWriter.Write(",p");
            streamWriter.Write(i.ToString("D2", CultureInfo.InvariantCulture));
        }

        streamWriter.WriteLine();
    }

    public void WriteRow(
        StreamWriter streamWriter,
        ReadOnlyMemory<char> directory,
        ReadOnlySpan<char> fileName,
        ulong endOfFile,
        ulong allocationSize,
        ulong fileId)
    {
        this.stringBuilder.Clear();

        ReadOnlySpan<char> extension = Path.GetExtension(fileName);
        if (extension.Length > 1 &&
            extension[0] == '.')
        {
            extension = extension[1..];
        }

        ReadOnlySpan<char> mimeType;
        ReadOnlySpan<char> mimeSubtype;

        if (MimeTypeMap.TryGetMimeType(new string(extension), out string mimeTypeString))
        {
            ReadOnlySpan<char> mimeTypeSpan = mimeTypeString;
            mimeType = mimeTypeSpan;
            mimeSubtype = ReadOnlySpan<char>.Empty;

            int mimeTypeSlashIndex = mimeTypeSpan.IndexOf('/');
            if (mimeTypeSlashIndex > 0)
            {
                mimeType = mimeTypeSpan[..mimeTypeSlashIndex];
                mimeSubtype = mimeTypeSpan[(mimeTypeSlashIndex + 1)..];
            }
        }
        else
        {
            mimeType = "unknown";
            mimeSubtype = extension;
        }

        this.stringBuilder.Append(fileId);
        this.stringBuilder.Append(',');
        this.stringBuilder.Append(endOfFile);
        this.stringBuilder.Append(',');
        this.stringBuilder.Append(allocationSize);
        this.stringBuilder.Append(',');
        EscapeAndWrite(this.stringBuilder, extension);
        this.stringBuilder.Append(',');
        EscapeAndWrite(this.stringBuilder, mimeType);
        this.stringBuilder.Append(',');
        EscapeAndWrite(this.stringBuilder, mimeSubtype);
        this.stringBuilder.Append(',');

        int from = this.stringBuilder.Length;

        if (directory.Equals(this.lastDirectory))
        {
            this.stringBuilder.Append(this.lastDirectorySerialized);
        }
        else
        {
            WriteDirectoryPath(this.stringBuilder, directory.Span);
            this.lastDirectory = directory;
            this.lastDirectorySerialized = this.stringBuilder.ToString(from, this.stringBuilder.Length - from).AsMemory();
        }

        EscapeAndWrite(this.stringBuilder, fileName);
        streamWriter.WriteLine(this.stringBuilder);
    }

    private static void WriteDirectoryPath(StringBuilder stringBuilder, ReadOnlySpan<char> input)
    {
        int level = 0;

        while (!input.IsEmpty)
        {
            level++;

            if (level > MaxLevels)
            {
                EscapeAndWrite(stringBuilder, input);
                stringBuilder.Append(Path.DirectorySeparatorChar);
                return;
            }

            int nextSeparatorIndex = input.IndexOf(Path.DirectorySeparatorChar);

            if (nextSeparatorIndex < 0)
            {
                EscapeAndWrite(stringBuilder, input);
                stringBuilder.Append(',');
                return;
            }

            EscapeAndWrite(stringBuilder, input[..nextSeparatorIndex]);
            stringBuilder.Append(',');
            input = input[(nextSeparatorIndex + 1)..];
        }
    }

    private static void EscapeAndWrite(StringBuilder stringBuilder, ReadOnlySpan<char> input)
    {
        bool needsCommaEscaping = input.IndexOf(',') >= 0;
        bool needsQuoteEscaping = input.IndexOf('"') >= 0;

        if (!(needsCommaEscaping || needsQuoteEscaping))
        {
            stringBuilder.Append(input);
            return;
        }

        stringBuilder.Append('"');

        if (needsQuoteEscaping)
        {
            stringBuilder.Append(new string(input).Replace("\"", "\"\""));
        }
        else
        {
            stringBuilder.Append(input);
        }

        stringBuilder.Append('"');
    }
}
