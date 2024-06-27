using System.Diagnostics;
using System.Threading.Tasks;

public class ProcessRunner
{
    public class ProcessRunResult
    {
        public Process Process { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string ErrorOutput { get; set; }
    }

    public static ProcessRunResult Run(string executableName, string arguments = null, string workingDirectory = null)
    {
        var task = RunAsync(executableName, arguments, workingDirectory);
        task.Wait();
        return task.Result;
    }

    public static Task<ProcessRunResult> RunAsync(string executableName, string arguments = null, string workingDirectory = null)
    {
        var processStartInfo = CreateProcessStartInfo(executableName, arguments, workingDirectory);
        return RunAsync(processStartInfo);
    }

    public static async Task<ProcessRunResult> RunAsync(ProcessStartInfo processStartInfo)
    {
        Process process = Process.Start(processStartInfo);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var result = new ProcessRunResult()
        {
            Process = process
        };

        await Task.WhenAll(outputTask, errorTask);

        if (!process.HasExited)
        {
            process.WaitForExit();
        }

        result.Output = outputTask.Result;
        result.ErrorOutput = errorTask.Result;
        result.ExitCode = process.ExitCode;

        return result;
    }

    private static ProcessStartInfo CreateProcessStartInfo(string filePath, string arguments = null, string workingDirectory = null)
    {
        filePath = filePath.QuoteIfNeeded();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        if (!string.IsNullOrEmpty(arguments))
        {
            processStartInfo.Arguments = arguments;
        }

        return processStartInfo;
    }
}
