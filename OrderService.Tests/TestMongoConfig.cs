using System;
using System.IO;

namespace OrderService.Tests;

internal static class TestMongoConfig
{
    public static string GetConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("OrderDbSettings__ConnectionString")
                  ?? Environment.GetEnvironmentVariable("MongoDbSettings__ConnectionString");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var envFile = Path.Combine(dir.FullName, ".env");
            if (File.Exists(envFile))
            {
                foreach (var rawLine in File.ReadAllLines(envFile))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("OrderDbSettings__ConnectionString=", StringComparison.Ordinal) ||
                        line.StartsWith("MongoDbSettings__ConnectionString=", StringComparison.Ordinal))
                    {
                        var val = line[(line.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val;
                        }
                    }
                }
            }
            dir = dir.Parent;
        }

        return "mongodb://localhost:27017";
    }
}
