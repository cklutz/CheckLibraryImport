# CheckLibraryImport

[![.NET](https://github.com/cklutz/CheckLibraryImport/actions/workflows/dotnet.yml/badge.svg)](https://github.com/cklutz/CheckLibraryImport/actions/workflows/dotnet.yml)

Check whether `[LibraryImport]` references a known export in a known library.

While migrating `[DllImport]`, using the Microsoft provided fix, I have encountered some
occurrences, where the DllImport specification was not explicit enough and thus resulted
in wrong symbols.

For example, consider the following

```csharp
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool LogonUser(
            string lpszUserName,
            string lpszDomain,
            string lpszPassword,
            int dwLogonType,
            int dwLogonProvider,
            out IntPtr phToken);
```

was converted into

```csharp
        [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool LogonUser(
            string lpszUserName,
            string lpszDomain,
            string lpszPassword,
            int dwLogonType,
            int dwLogonProvider,
            out IntPtr phToken);
```

However, the entry point `LogonUser` does not exist in `Kernel32.dll`, but rather it must be
`LogonUserW` (for Unicode, as above), or `LogonUserA` for Ansi.

This tool simply iterates a list of presented managed assemblies and does the following:

* Check every function in the assembly, whether it is attributed with `[LibraryImport]`
* If that is the case, see if `EntryPoint` is specified explicitly, otherwise assume the function name is the entry point.
* Use that entry point and `LoadLibrary`/`GetProcAddress` the entry point in the library. If that fails, report an error.

If you encounter errors running this tool on your libraries check if the `[LibraryImport]` specification
is maybe lacking an explicit `EntryPoint`.
