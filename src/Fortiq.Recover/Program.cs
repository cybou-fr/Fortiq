namespace Fortiq.Recover;
public static class Program
{
    public static Task<int> Main(string[] args) => RecoveryCli.RunAsync(args, new RecoveryCommandExecutor(), Console.Out, Console.Error, CancellationToken.None);
}
