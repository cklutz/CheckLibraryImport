using McMaster.Extensions.CommandLineUtils;
using Mono.Cecil;
using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;

namespace CheckLibraryImport
{
    internal partial class Program
    {
        static int Main(string[] args)
        {
            var app = new CommandLineApplication
            {
                Name = typeof(Program).Assembly.GetName().Name,
                Error = Console.Error
            };

            var directory = app.Argument("STARTDIR", "The directory to search for files in.").IsRequired();
            var noWarn = app.Option("--no-warn", "Do not warn about obscure ntdll.dll/WoW64 issues.", CommandOptionType.NoValue);
            var pattern = app.Option("--pattern <PATTERN>", "Use glob-style file pattern instead of default '.dll'.", CommandOptionType.SingleValue, o => o.DefaultValue = "*.dll");
            app.HelpOption("--help");

            try
            {
                _ = app.Parse(args);
                var res = app.GetValidationResult();
                if (res != ValidationResult.Success)
                {
                    throw new CommandParsingException(app, res!.ErrorMessage ?? "no message");
                }
            }
            catch (CommandParsingException ex)
            {
                Console.Error.WriteLine($"error: usage: {ex.Message}");
                app.ShowHelp();
                return ex.HResult;
            }

            int errors = 0;
            int warnings = 0;
            int total = 0;
            int rc = 0;

            try
            {
                foreach (string assembly in Directory.EnumerateFiles(directory.Value!, pattern.Value()!))
                {
                    CheckAssembly(assembly, ref errors, ref warnings, noWarn.HasValue());
                    total++;
                }

                if (errors > 0)
                {
                    Console.Error.WriteLine($"error: found {errors:N0} errors.");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex}");
                rc = ex.HResult;
            }
            finally
            {
                rc = errors > 0 ? 1 : rc;
                Console.WriteLine($"checked {total:N0} files, with {errors:N0} errors and {warnings:N0} warnings.");
            }

            return rc;
        }

        static void CheckAssembly(string fileName, ref int errors, ref int warnings, bool noWarn)
        {
            string displayFileName = Path.GetFileName(fileName);
            AssemblyDefinition def;
            try
            {
                def = AssemblyDefinition.ReadAssembly(fileName);
            }
            catch (BadImageFormatException)
            {
                return;
            }

            foreach (var type in def.Modules.SelectMany(m => m.Types))
            {
                foreach (var method in type.Methods)
                {
                    // Do not bother with reading symbols. They are not useful for LibraryImportAttribute, because the
                    // source will always be the generated code. But when you need to fix it, you have to do it at the
                    // original "partial" location. This is much easier and more precisely expressed when we just dump
                    // the name of the partial function where the LibraryImportAttribute was applied to.
                    string displayContext = displayFileName + ", " + type.FullName + "." + method.Name + "()";

                    var attr = method.CustomAttributes.FirstOrDefault(ca => ca.AttributeType.FullName == typeof(LibraryImportAttribute).FullName);
                    if (attr != null)
                    {
                        string entryPoint = GetPropertyArgument(attr, nameof(LibraryImportAttribute.EntryPoint)) ?? method.Name;
                        string? nativeLib = attr.ConstructorArguments[0].Value?.ToString();
                        if (nativeLib == null)
                        {
                            Console.Error.WriteLine($"error: {displayContext}: missing LibraryName");
                            errors++;
                            continue;
                        }

                        if (IsWow64NtDllExport(entryPoint, nativeLib) && Environment.Is64BitProcess)
                        {
                            if (!noWarn)
                            {
                                Console.Error.WriteLine($"warning: {displayContext}: ignored because WOW64 not supported from a 64 bit process.");
                                warnings++;
                            }
                            continue;
                        }

                        bool needFree = false;
                        IntPtr module = LoadModule(nativeLib, ref needFree);
                        try
                        {
                            if (module == IntPtr.Zero)
                            {
                                Console.Error.WriteLine($"error: {displayContext}: module '{nativeLib}': {Marshal.GetLastPInvokeErrorMessage()}");
                                errors++;
                            }
                            else
                            {
                                var proc = GetProcAddress(module, entryPoint);
                                if (proc == IntPtr.Zero)
                                {
                                    string msg = $"error: {displayContext}: '{entryPoint}' in module '{nativeLib}': {Marshal.GetLastPInvokeErrorMessage()}";
                                    errors++;

                                    // Check alternatives
                                    proc = GetProcAddress(module, entryPoint + "W");
                                    if (proc != IntPtr.Zero)
                                    {
                                        msg += $"\r\n\tPotential candidate: {entryPoint}W";
                                    }
                                    proc = GetProcAddress(module, entryPoint + "A");
                                    if (proc != IntPtr.Zero)
                                    {
                                        msg += $"\r\n\tPotential candidate: {entryPoint}A";
                                    }

                                    Console.Error.WriteLine(msg);
                                }
                            }
                        }
                        finally
                        {
                            if (module != IntPtr.Zero && needFree)
                            {
                                FreeLibrary(module);
                            }
                        }
                    }
                }
            }
        }

        private static nint LoadModule(string nativeLib, ref bool needFree)
        {
            nint module;
            if (IsInEveryNetProcess(nativeLib))
            {
                module = GetModuleHandle(nativeLib);
            }
            else
            {
                module = LoadLibrary(nativeLib);
                needFree = true;
            }

            return module;
        }

        private static bool IsWow64NtDllExport(string entryPoint, string nativeLib)
        {
            // Actual location: C:\Windows\SysWOW64\ntdll.dll. However, we cannot load it from inside a 64 bit process.
            // Thus simply flag this as "unchecked" and get on with the next occurrence.
            // This situation should be pretty rare anyway.

            return Path.GetFileNameWithoutExtension(nativeLib) == "ntdll" && (entryPoint.StartsWith("NtWow64") || entryPoint.StartsWith("ZwWow64"));
        }

        private static bool IsInEveryNetProcess(string nativeLib)
        {
            // ntdll.dll and kernel32.dll is loaded by every process on Windows, so we can simply get the module handle here.
            return Path.GetFileNameWithoutExtension(nativeLib).Equals("kernel32", StringComparison.OrdinalIgnoreCase) ||
                   Path.GetFileNameWithoutExtension(nativeLib).Equals("ntdll", StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetPropertyArgument(CustomAttribute attr, string name)
        {
            var res = attr.Properties.Where(p => p.Name == name).ToList();
            if (res.Count() > 0 && res[0].Argument.Value != null)
            {
                return res[0].Argument.Value.ToString();
            }

            return null;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeLibrary([In] IntPtr hModule);
    }
}
