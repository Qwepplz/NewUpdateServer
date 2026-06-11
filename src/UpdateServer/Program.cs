using System;
using UpdateServer.App;

namespace UpdateServer
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return new UpdateServerApplication().Run(args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
