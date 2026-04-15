using UpdateServer.App;

namespace UpdateServer
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            return new UpdateServerApplication().Run(args);
        }
    }
}
