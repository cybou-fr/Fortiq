namespace Fortiq.Recover;

public static class Program
{
    public static Task<int> Main(string[] args) => RecoveryCli.RunAsync(
        args,
        new RecoveryCommandExecutor(),
        new ConsoleRecoveryMaterialReader(Console.In, Console.Error),
        Console.Out,
        Console.Error,
        CancellationToken.None);
}
