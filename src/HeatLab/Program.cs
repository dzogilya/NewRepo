using System.Text;
using HeatLab.Gateway;
using HeatLab.Server;

Console.OutputEncoding = Encoding.UTF8;

try
{
    if (args.Length >= 4 && args[0] == "server")
    {
        await ServerApp.RunAsync(serverId: args[1], port: int.Parse(args[2]), gatewayPid: int.Parse(args[3]));
    }
    else
    {
        await GatewayApp.RunAsync(args);
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nПОМИЛКА: {ex.GetBaseException().Message}");
    Console.ResetColor();
    if (args.FirstOrDefault() != "server")
    {
        Console.WriteLine("Вікно закриється через 30 с.");
        Thread.Sleep(30_000);
    }
    Environment.Exit(1);
}
