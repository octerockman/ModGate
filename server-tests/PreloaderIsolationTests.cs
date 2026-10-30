using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PreloaderIsolationTests
{
    internal static void Run(Action<string, Action> test, string? preloaderPath = null)
    {
        var path = preloaderPath ?? FindPreloader();
        test("preloader includes health wire validation without the live sampler or process collector", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var module = assembly.MainModule;
            if (module.GetType("ModGate.ServerHealthSnapshot") == null || module.GetType("ModGate.ServerHealth") == null)
                throw new Exception("The health wire contract must be available for protocol validation.");
            if (module.GetType("ModGate.ServerHealthSampler") != null || module.GetType("ModGate.ServerHealthReadings") != null ||
                module.GetType("ModGate.ServerHealth").Methods.Any(m => m.Name == "ReadCurrentProcess"))
                throw new Exception("Live server health collection must remain outside the preloader.");
        });
        test("compiled preloader has no managed HTTP assembly, type, or member references", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var forbidden = new List<string>();
            foreach (var module in assembly.Modules)
            {
                forbidden.AddRange(module.AssemblyReferences.Where(a => IsHttpAssembly(a.Name)).Select(a => "assembly " + a.FullName));
                forbidden.AddRange(module.GetTypeReferences().Where(IsHttpType).Select(t => "type " + t.FullName));
                foreach (var member in module.GetMemberReferences())
                {
                    if (IsHttpType(member.DeclaringType) || member.FullName.Contains("System.Net.Http.", StringComparison.Ordinal))
                        forbidden.Add("member " + member.FullName);
                }
            }
            if (forbidden.Count != 0) throw new Exception("Preloader must not select a managed HTTP implementation: " + string.Join("; ", forbidden.Distinct()));
        });
        test("preloader HTTP diagnostics inspect loaded assemblies without forcing loads", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            var methods = assembly.Modules.SelectMany(m => m.Types).SelectMany(AllTypes).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            var diagnostic = methods.Values.SingleOrDefault(m => m.DeclaringType.FullName == "ModGate.NetworkAssemblyInfo" && m.Name == "LoadedHttp");
            if (diagnostic == null) return;
            var pending = new Queue<MethodDefinition>();
            var visited = new HashSet<string>();
            var enumeratesLoaded = false;
            pending.Enqueue(diagnostic);
            while (pending.Count > 0)
            {
                var method = pending.Dequeue();
                if (!visited.Add(method.FullName) || !method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference type)
                        throw new Exception("HTTP diagnostics must not resolve a type token: " + type.FullName);
                    if (instruction.Operand is not MethodReference called) continue;
                    var owner = called.DeclaringType.FullName;
                    if (owner == "System.AppDomain" && called.Name == "GetAssemblies") enumeratesLoaded = true;
                    if (ForcesLoad(owner, called.Name)) throw new Exception("HTTP diagnostics must not force runtime loading: " + called.FullName);
                    if (methods.TryGetValue(called.GetElementMethod().FullName, out var local)) pending.Enqueue(local);
                }
            }
            if (!enumeratesLoaded) throw new Exception("HTTP diagnostics should enumerate assemblies already loaded in the current domain.");
        });
    }

    private static bool IsHttpAssembly(string name) => name == "System.Net.Http" || name.StartsWith("System.Net.Http.", StringComparison.Ordinal);
    private static bool IsHttpType(TypeReference type)
    {
        if (type.Namespace == "System.Net.Http" || type.Namespace.StartsWith("System.Net.Http.", StringComparison.Ordinal)) return true;
        if (type.Scope is AssemblyNameReference scope && IsHttpAssembly(scope.Name)) return true;
        if (type is GenericInstanceType generic && generic.GenericArguments.Any(IsHttpType)) return true;
        return type is TypeSpecification specification && IsHttpType(specification.ElementType);
    }
    private static bool ForcesLoad(string owner, string method) =>
        owner == "System.Reflection.Assembly" && (method.StartsWith("Load", StringComparison.Ordinal) || method == "GetType" || method == "CreateInstance") ||
        owner == "System.Type" && method == "GetType" ||
        owner == "System.AppDomain" && (method == "Load" || method.StartsWith("CreateInstance", StringComparison.Ordinal)) ||
        owner == "System.Activator" && method.StartsWith("CreateInstance", StringComparison.Ordinal) ||
        owner.StartsWith("System.Runtime.Loader.AssemblyLoadContext", StringComparison.Ordinal) && method.StartsWith("Load", StringComparison.Ordinal) ||
        owner == "System.Runtime.CompilerServices.RuntimeHelpers" && method == "RunClassConstructor";

    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(AllTypes)) yield return nested;
    }
    private static string FindPreloader()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? folder = new DirectoryInfo(start); folder != null; folder = folder.Parent)
            {
                var project = Path.Combine(folder.FullName, "src", "ValheimModGate.Preloader");
                if (!File.Exists(Path.Combine(project, "ValheimModGate.Preloader.csproj"))) continue;
                var path = Path.Combine(project, "bin", "Release", "netstandard2.0", "ValheimModGate.Preloader.dll");
                if (!File.Exists(path)) throw new FileNotFoundException("Build the Release preloader before running its isolation checks.", path);
                return path;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the preloader project for compiled isolation checks.");
    }
}
