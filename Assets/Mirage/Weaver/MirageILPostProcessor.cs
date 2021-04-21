using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace Mirage.Weaver
{
    public class MirageILPostProcessor : ILPostProcessor
    {
        public const string RuntimeAssemblyName = "Mirage";

        public override ILPostProcessor GetInstance() => this;

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            bool willProcess = WillProcess(compiledAssembly);

            Console.WriteLine($"Mirage ILPP: Checking: {compiledAssembly.Name} will process: {willProcess}");
            if (!willProcess)
                return null;

            var logger = new Logger();
            var weaver = new Weaver(logger);

            Console.WriteLine($"Mirage ILPP: Weave Started on {compiledAssembly.Name}");
            AssemblyDefinition assemblyDefinition = weaver.Weave(compiledAssembly);
            Console.WriteLine($"Mirage ILPP: Weave Finished on {compiledAssembly.Name}");

            // write
            var pe = new MemoryStream();
            var pdb = new MemoryStream();

            var writerParameters = new WriterParameters
            {
                SymbolWriterProvider = new PortablePdbWriterProvider(),
                SymbolStream = pdb,
                WriteSymbols = true
            };

            assemblyDefinition?.Write(pe, writerParameters);

            return new ILPostProcessResult(new InMemoryAssembly(pe.ToArray(), pdb.ToArray()), logger.Diagnostics);
        }

        public override bool WillProcess(ICompiledAssembly compiledAssembly) =>
            compiledAssembly.References.Any(filePath => Path.GetFileNameWithoutExtension(filePath) == RuntimeAssemblyName);
    }
}
