using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

string root = Environment.GetEnvironmentVariable("PPA_TEST_ROOT")!;
string mode = Environment.GetEnvironmentVariable("PPA_TEST_MODE")!;
if (args[0] == "--update-recovered") { File.WriteAllText(Path.Combine(root, "recovered.txt"), File.ReadAllText(Path.Combine(root, "version.txt"))); return; }
if (args[0] == "--update-started")
{
    if (mode == "startup-failure") return;
    using var client = new NamedPipeClientStream(".", "PPA-restart-" + args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await client.ConnectAsync(timeout.Token);
    using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true);
    using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
    string start = (await reader.ReadLineAsync(timeout.Token))!;
    await writer.WriteLineAsync("HEALTHY " + start[6..]);
    if (await reader.ReadLineAsync(timeout.Token) != "SUCCESS") throw new Exception("No success acknowledgement");
    File.WriteAllText(Path.Combine(root, "healthy.txt"), File.ReadAllText(Path.Combine(root, "version.txt"))); return;
}
string token = Guid.NewGuid().ToString("N");
var info = new ProcessStartInfo(args[1]) { UseShellExecute = true };
foreach (string arg in new[] { "--silent-update", "--handoff", token, "--parent", Environment.ProcessId.ToString(), "--parent-start", Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString() }) info.ArgumentList.Add(arg);
using var installer = Process.Start(info)!;
File.WriteAllText(Path.Combine(root, "installer-pid.txt"), installer.Id.ToString());
using var pipe = new NamedPipeClientStream(".", "PPA-update-" + token, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await pipe.ConnectAsync(deadline.Token);
using var input = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
using var output = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
if (!(await input.ReadLineAsync(deadline.Token))!.StartsWith("READY ")) throw new Exception("Not ready");
if (mode == "cancel") return;
await output.WriteLineAsync("COMMIT");
if (await input.ReadLineAsync(deadline.Token) != "COMMITTED") throw new Exception("No commit acknowledgement");
// The parent exits here. The real installer code must continue without it.
