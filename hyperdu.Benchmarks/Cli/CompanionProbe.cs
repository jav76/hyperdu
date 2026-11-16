using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace hyperdu.Benchmarks.Cli;

/// <summary>
/// Measures Time-to-First-Line (TTFL), Peak RSS memory, line counts, and output parity outside hyperfine loops.
/// </summary>
internal static partial class CompanionProbe
{
    public static async Task<ProbeResult> ProbeAsync(
        ScenarioDefinition scenario,
        CommandInvocation invocation,
        ProbeResult? referenceProbe,
        CancellationToken cancellationToken = default)
    {
        (int exitCode, double? ttflMs, long outputLines, string stdoutHash, string stdoutSample, string stderrSample) =
            await RunStreamingProbeAsync(invocation, cancellationToken);

        long peakRssKb = await MeasurePeakRssKbAsync(invocation, cancellationToken);
        double? earlyExitMs = scenario.MeasureEarlyExit
            ? await MeasureEarlyExitHeadAsync(invocation, maxLines: 100, cancellationToken)
            : null;

        ProbeResult raw = new(
            ExitCode: exitCode,
            TtflMs: ttflMs,
            PeakRssKb: peakRssKb,
            OutputLines: outputLines,
            StdoutHash: stdoutHash,
            StdoutSample: stdoutSample,
            StderrSample: stderrSample,
            ParityMatched: true,
            ParityNote: "ok",
            EarlyExitHead100Ms: earlyExitMs);

        (bool parityMatched, string parityNote) = EvaluateParity(scenario, invocation, raw, referenceProbe);
        return raw with { ParityMatched = parityMatched, ParityNote = parityNote };
    }

    /// <summary>
    /// Simulates a `| head -n N` consumer by reading <paramref name="maxLines"/> from stdout,
    /// closing the read pipe to trigger SIGPIPE/EPIPE, and measuring total time until the child exits.
    /// </summary>
    public static async Task<double?> MeasureEarlyExitHeadAsync(
        CommandInvocation invocation,
        int maxLines = 100,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = BuildStartInfo(invocation, redirectStdout: true, redirectStderr: true);
        long startTimestamp = Stopwatch.GetTimestamp();

        using Process? process = Process.Start(psi);
        if (process == null)
            return null;

        Task drainErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);

        byte[] buffer = new byte[4096];
        long lines = 0;
        Stream stdout = process.StandardOutput.BaseStream;

        try
        {
            while (lines < maxLines)
            {
                int read = await stdout.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] is (byte)'\n' or 0)
                    {
                        lines++;
                        if (lines >= maxLines)
                            break;
                    }
                }
            }
        }
        finally
        {
            // Closing stdout triggers SIGPIPE/EPIPE on the next child write, matching `| head`.
            process.StandardOutput.Close();
        }

        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Task.WhenAll(drainErr, process.WaitForExitAsync(waitCts.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
        }

        return Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
    }

    internal static (bool Matched, string Note) EvaluateParity(
        ScenarioDefinition scenario,
        CommandInvocation invocation,
        ProbeResult candidate,
        ProbeResult? referenceProbe)
    {
        if (invocation.Tool.IsReferenceDu)
        {
            return candidate.ExitCode == 0
                ? (true, "baseline")
                : (false, $"exit {candidate.ExitCode}");
        }

        if (scenario.RequireStrictStdoutParity && referenceProbe != null)
        {
            if (candidate.ExitCode != referenceProbe.ExitCode)
                return (false, $"exit mismatch ({candidate.ExitCode} vs {referenceProbe.ExitCode})");

            if (!string.Equals(candidate.StdoutHash, referenceProbe.StdoutHash, StringComparison.Ordinal))
                return (false, $"stdout diff (lines {candidate.OutputLines} vs {referenceProbe.OutputLines})");

            return (true, "exact match");
        }

        if (candidate.ExitCode != 0)
            return (false, $"exit {candidate.ExitCode}");

        if (candidate.OutputLines == 0 && string.IsNullOrWhiteSpace(candidate.StdoutSample))
            return (false, "empty output");

        return (true, "ok (exit 0)");
    }

    private static async Task<(int ExitCode, double? TtflMs, long OutputLines, string StdoutHash, string StdoutSample, string StderrSample)>
        RunStreamingProbeAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = BuildStartInfo(invocation, redirectStdout: true, redirectStderr: true);
        long startTimestamp = Stopwatch.GetTimestamp();

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{invocation.Tool.Executable}'.");

        Task<(string Sample, string Hash)> stderrTask = DrainStreamSampleAsync(process.StandardError.BaseStream, cancellationToken);

        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        byte[] sampleBytes = new byte[1024];
        int sampleCount = 0;
        double? ttflMs = null;
        long outputLines = 0;
        bool hasAnyBytes = false;
        bool lastByteWasTerminator = false;

        Stream stdout = process.StandardOutput.BaseStream;
        while (true)
        {
            int read = await stdout.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            hasAnyBytes = true;
            ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
            sha256.AppendData(chunk);

            if (sampleCount < sampleBytes.Length)
            {
                int toCopy = Math.Min(read, sampleBytes.Length - sampleCount);
                chunk[..toCopy].CopyTo(sampleBytes.AsSpan(sampleCount));
                sampleCount += toCopy;
            }

            foreach (byte b in chunk)
            {
                if (b is (byte)'\n' or 0)
                {
                    ttflMs ??= Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    outputLines++;
                }
            }

            lastByteWasTerminator = chunk[^1] is (byte)'\n' or 0;
        }

        if (hasAnyBytes)
        {
            ttflMs ??= Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            if (!lastByteWasTerminator)
                outputLines++;
        }

        await process.WaitForExitAsync(cancellationToken);
        (string stderrSample, _) = await stderrTask;

        string hashHex = Convert.ToHexStringLower(sha256.GetHashAndReset());
        string stdoutSample = Encoding.UTF8.GetString(sampleBytes, 0, sampleCount).TrimEnd('\r', '\n', '\0');
        return (process.ExitCode, ttflMs, outputLines, hashHex, stdoutSample, stderrSample);
    }

    private static async Task<long> MeasurePeakRssKbAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (File.Exists("/usr/bin/time"))
        {
            long? measured = OperatingSystem.IsMacOS()
                ? await MeasurePeakRssMacTimeAsync(invocation, cancellationToken)
                : await MeasurePeakRssGnuTimeAsync(invocation, cancellationToken);
            if (measured.HasValue)
                return measured.Value;
        }

        if (!OperatingSystem.IsWindows())
        {
            string? python = ComparatorRegistry.FindOnPath("python3");
            if (python != null)
            {
                long? measured = await MeasurePeakRssPythonWait4Async(python, invocation, cancellationToken);
                if (measured.HasValue)
                    return measured.Value;
            }
        }

        return await MeasurePeakWorkingSetPollingAsync(invocation, cancellationToken);
    }

    private static async Task<long?> MeasurePeakRssGnuTimeAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"hyperdu-rss-{Guid.NewGuid():N}.txt");
        try
        {
            ProcessStartInfo psi = new("/usr/bin/time")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("%M");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tempFile);
            psi.ArgumentList.Add(invocation.Tool.Executable);
            foreach (string arg in invocation.Arguments)
                psi.ArgumentList.Add(arg);
            ApplyEnvironment(psi, invocation.EnvironmentVariables);

            using Process? process = Process.Start(psi);
            if (process == null)
                return null;

            Task drainOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            Task drainErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await Task.WhenAll(drainOut, drainErr, process.WaitForExitAsync(cancellationToken));

            if (!File.Exists(tempFile))
                return null;

            string text = (await File.ReadAllTextAsync(tempFile, cancellationToken)).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb) ? kb : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task<long?> MeasurePeakRssMacTimeAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo psi = new("/usr/bin/time")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(invocation.Tool.Executable);
            foreach (string arg in invocation.Arguments)
                psi.ArgumentList.Add(arg);
            ApplyEnvironment(psi, invocation.EnvironmentVariables);

            using Process? process = Process.Start(psi);
            if (process == null)
                return null;

            Task drainOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            Task<string> readErr = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(drainOut, readErr, process.WaitForExitAsync(cancellationToken));

            Match match = MacMaxRssRegex().Match(readErr.Result);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes))
            {
                return Math.Max(1, bytes / 1024);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<long?> MeasurePeakRssPythonWait4Async(
        string pythonBin,
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            const string script =
                "import os, subprocess, sys\n" +
                "p = subprocess.Popen(sys.argv[1:], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)\n" +
                "_, _, ru = os.wait4(p.pid, 0)\n" +
                "print(ru.ru_maxrss)";

            ProcessStartInfo psi = new(pythonBin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(invocation.Tool.Executable);
            foreach (string arg in invocation.Arguments)
                psi.ArgumentList.Add(arg);
            ApplyEnvironment(psi, invocation.EnvironmentVariables);

            using Process? process = Process.Start(psi);
            if (process == null)
                return null;

            string output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (!long.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out long rawRss))
                return null;

            // Linux reports ru_maxrss in KiB; macOS reports bytes.
            return OperatingSystem.IsMacOS() ? Math.Max(1, rawRss / 1024) : rawRss;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<long> MeasurePeakWorkingSetPollingAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = BuildStartInfo(invocation, redirectStdout: true, redirectStderr: true);
        using Process? process = Process.Start(psi);
        if (process == null)
            return 0;

        Task drainOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        Task drainErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);

        long maxBytes = 0;
        while (!process.HasExited)
        {
            try
            {
                process.Refresh();
                if (process.PeakWorkingSet64 > maxBytes)
                    maxBytes = process.PeakWorkingSet64;
            }
            catch
            {
                break;
            }

            await Task.Delay(1, cancellationToken);
        }

        await Task.WhenAll(drainOut, drainErr, process.WaitForExitAsync(cancellationToken));
        return maxBytes / 1024;
    }

    private static async Task<(string Sample, string Hash)> DrainStreamSampleAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[8192];
        byte[] sample = new byte[1024];
        int sampleLen = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
            sha256.AppendData(chunk);
            if (sampleLen < sample.Length)
            {
                int copy = Math.Min(read, sample.Length - sampleLen);
                chunk[..copy].CopyTo(sample.AsSpan(sampleLen));
                sampleLen += copy;
            }
        }

        return (Encoding.UTF8.GetString(sample, 0, sampleLen).Trim(), Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    internal static ProcessStartInfo BuildStartInfo(
        CommandInvocation invocation,
        bool redirectStdout,
        bool redirectStderr)
    {
        ProcessStartInfo psi = new(invocation.Tool.Executable)
        {
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = redirectStderr,
            UseShellExecute = false
        };
        foreach (string arg in invocation.Arguments)
            psi.ArgumentList.Add(arg);
        ApplyEnvironment(psi, invocation.EnvironmentVariables);
        return psi;
    }

    private static void ApplyEnvironment(ProcessStartInfo psi, IReadOnlyDictionary<string, string> env)
    {
        foreach ((string key, string value) in env)
            psi.Environment[key] = value;
    }

    [GeneratedRegex(@"(\d+)\s+maximum resident set size", RegexOptions.IgnoreCase)]
    private static partial Regex MacMaxRssRegex();
}
