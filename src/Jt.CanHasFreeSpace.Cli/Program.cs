// Copyright 2022 Jason Thorsness
using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Jt.CanHasFreeSpace.Cli;

Option<string> includeOption = new(
    new[] { "--include", "-i" },
    x =>
    {
        string? includeValue = x.Tokens?.SingleOrDefault()?.Value;

        if (includeValue == null)
        {
            string? defaultInclude = null;

            string location = Assembly.GetExecutingAssembly().Location;
            if (location.Length > 0)
            {
                defaultInclude = Path.GetPathRoot(location);
            }

            if (defaultInclude == null)
            {
                x.ErrorMessage = "No --include provided and program not running under valid location";
                return null!;
            }

            includeValue = defaultInclude;
        }

        if (!Directory.Exists(includeValue))
        {
            x.ErrorMessage = $"Directory for --include/-i does not exist: {includeValue}";
            return null!;
        }

        return includeValue;
    },
    isDefault: true,
    "A include to read from. If not supplied, will read from the include of the drive hosting this program.")
{
    Arity = ArgumentArity.ZeroOrOne,
};

Option<string> outputOption = new(
    new[] { "--output", "-o" },
    () => Path.Combine(Path.GetTempPath(), "CanHasFreeSpace", "data.csv"),
    "A relative or absolute path to the output file. If not supplied, will output to a temporary location.")
{
    Arity = ArgumentArity.ZeroOrOne,
};

RootCommand rootCommand = new()
{
    includeOption,
    outputOption,
};

rootCommand.Description = "Generates a CSV of allocation data from Windows file systems.";

rootCommand.SetHandler(
    async (string include, string output) =>
    {
        Console.WriteLine("Include: ");
        Console.WriteLine($" {include}");

        Console.WriteLine("Output: ");
        Console.WriteLine($" {output}");

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new ArgumentException($"Invalid output path: {output}"));

        Channel<MemoryStream> outputChannel = Channel.CreateUnbounded<MemoryStream>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

        const int maxBufferLength = 4 * 1024 * 1024;
        const int maxBuffers = 2;

        using SemaphoreSlim semaphoreSlim = new(maxBuffers);

        ConcurrentBag<MemoryStream> available = new();

        Task outputTask = Task.Run(async () =>
        {
            await using FileStream fileStream = new(output, FileMode.Create, FileAccess.Write);
            await foreach (MemoryStream memoryStream in outputChannel.Reader.ReadAllAsync())
            {
                semaphoreSlim.Release();
                await memoryStream.CopyToAsync(fileStream);
                memoryStream.SetLength(0);
                available.Add(memoryStream);
                await fileStream.FlushAsync();
            }
        });

        MemoryStream current = new(maxBufferLength);
        StreamWriter currentStreamWriter = new(current, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, true);

        void PushCurrentBuffer()
        {
            semaphoreSlim.Wait();
            currentStreamWriter.Flush();
            currentStreamWriter.Dispose();
            current.Position = 0;
            outputChannel.Writer.TryWrite(current);

            if (!available.TryTake(out MemoryStream? recycled))
            {
                recycled = new MemoryStream(maxBufferLength);
            }

            current = recycled;
            currentStreamWriter = new(current, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, true);
        }

        CsvWriter csvWriter = new();
        csvWriter.WriteHeader(currentStreamWriter);

        int count = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();

        WindowsFileEnumerator.Enumerate(
            include,
            (directory, fileName, endOfFile, allocationSize, fileId) =>
            {
                csvWriter.WriteRow(currentStreamWriter, directory, fileName, endOfFile, allocationSize, fileId);
                count++;

                if (current.Length > maxBufferLength)
                {
                    PushCurrentBuffer();
                    Console.WriteLine($"Progress {count} rows in {stopwatch.Elapsed}");
                }
            },
            (directory, error) =>
            {
                Console.WriteLine($"DIR SKIPPED ({error}): {directory}");
            });

        PushCurrentBuffer();
        outputChannel.Writer.Complete();
        await outputTask;

        Console.WriteLine($"Completed {count} rows in {stopwatch.Elapsed}");
    },
    includeOption,
    outputOption);

return rootCommand.Invoke(args);
