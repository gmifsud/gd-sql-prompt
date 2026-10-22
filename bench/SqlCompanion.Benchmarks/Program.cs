using BenchmarkDotNet.Running;

// Run: dotnet run -c Release --project bench/SqlCompanion.Benchmarks -- --filter '*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
