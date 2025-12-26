using System;
using System.Threading;

namespace EDDA.Server;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("EDDA Server starting on basement server...");
        Console.WriteLine($"Started at: {DateTime.Now}");
        Console.WriteLine($"Hostname: {Environment.MachineName}");
        Console.WriteLine($"User: {Environment.UserName}");
        Console.WriteLine();
        
        int count = 0;
        while (true)
        {
            count++;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] I'm running in the basement! (tick #{count})");
            Thread.Sleep(2000); // 2 seconds
        }
    }
}