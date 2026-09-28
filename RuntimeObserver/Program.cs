using QnocrPoc.RuntimeObserver;

if (args.Length is not (4 or 6 or 8) ||
    args[0] != "--target" || args[2] != "--pipe" ||
    (args.Length >= 6 && args[4] != "--polls") ||
    (args.Length == 8 && args[6] != "--interval-ms") ||
    (args.Length >= 6 && (!int.TryParse(args[5], out var parsedPolls) || parsedPolls < 1)) ||
    (args.Length == 8 && (!int.TryParse(args[7], out var parsedInterval) || parsedInterval < 0)))
{
    Console.Error.WriteLine("Usage: RuntimeObserver --target PROCESS_NAME --pipe PIPE_NAME [--polls N [--interval-ms N]]");
    return 2;
}

var polls = args.Length >= 6 ? int.Parse(args[5]) : int.MaxValue;
var intervalMs = args.Length == 8 ? int.Parse(args[7]) : 1000;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    await RuntimeObserverRunner.RunAsync(args[1], args[3], polls,
        TimeSpan.FromMilliseconds(intervalMs), cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Runtime observer stopped: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
