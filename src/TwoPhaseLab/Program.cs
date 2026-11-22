using MathTournament.TwoPhaseLab;
var folder=args.Length>0?Path.GetFullPath(args[0]):Path.Combine("test-results","2pc-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
Console.WriteLine(Example.Run(folder));
Console.WriteLine("Durable decisions and participant records: "+folder);
