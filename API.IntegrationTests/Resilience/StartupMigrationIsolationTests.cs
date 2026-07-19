using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace API.IntegrationTests.Resilience
{
    /// <summary>
    /// CRITICAL-PATH resilience test for <b>startup migration-failure isolation</b>, exercising the
    /// <b>real compiled production entry point</b> (<c>API/Program.cs</c> <c>Main</c>) rather than any
    /// test-local replica of its logic.
    ///
    /// <para>
    /// Production contract under test: <c>Main</c> wraps the EF Core <c>MigrateAsync</c> + seed for both
    /// the <c>StoreContext</c> and the <c>AppIdentityDbContext</c> in a single <c>try/catch (Exception)</c>
    /// that logs <c>"An error occured during migration"</c> (via the <c>API.Program</c> logger) and then
    /// <b>CONTINUES</b> to <c>host.Run()</c>. A migration failure must therefore be <b>isolated</b> — the
    /// web host must still start and listen rather than crash the process.
    /// </para>
    ///
    /// <para>
    /// <b>Why a child process (and not a replicated block).</b> An earlier iteration copied <c>Main</c>'s
    /// migrate/seed <c>try/catch</c> into a test-local method and asserted against the copy. That is a
    /// false-positive risk: production <c>Main</c> could remove its catch, reorder migrations, stop
    /// seeding/logging, or stop continuing to <c>host.Run()</c> while the copied-block test stayed green.
    /// This test instead launches the genuine build output <c>API.dll</c> — so <c>Program.Main</c> runs
    /// verbatim — points its database connection strings at an <b>unreachable endpoint</b> so
    /// <c>MigrateAsync</c> genuinely throws, and asserts on the <b>real</b> process behaviour:
    /// <list type="number">
    ///   <item>the production catch logs <c>"An error occured during migration"</c>;</item>
    ///   <item>the logged exception's stack trace originates from <c>API.Program.Main</c> (proving the
    ///         real entry point ran — the failure is not synthesised by the test);</item>
    ///   <item>the isolated failure is a genuine database connection failure (<c>Npgsql</c>); and</item>
    ///   <item>the host still reaches <c>host.Run()</c> and is <b>listening</b> on its port, while the
    ///         process remains alive (fail-closed continuity).</item>
    /// </list>
    /// No production file is modified: the AAP restricts production changes to a single annotated Stripe
    /// seam in <c>PaymentService</c>, so a <c>Program</c> seam is intentionally avoided; running the
    /// compiled entry point out-of-process is the in-scope way to test <c>Main</c> without touching it.
    /// </para>
    ///
    /// <para>
    /// <b>Isolation &amp; determinism.</b> The child runs in a <b>disposable temporary content root</b>
    /// (with its own <c>Content</c>/<c>wwwroot</c> directories) and with <c>HOME</c> redirected to that
    /// temporary tree so ASP.NET Core Data Protection writes its key ring under the throwaway directory
    /// instead of the machine-global <c>~/.aspnet/DataProtection-Keys</c>. The whole tree is deleted in a
    /// <c>finally</c> block, and the spawned process is killed by pid (never a broad signal). Readiness is
    /// established by <b>polling</b> the captured output and the TCP port (a wait strategy), never a fixed
    /// <c>Thread.Sleep</c>, so the test is deterministic and needs <b>no Docker/Testcontainers</b> (an
    /// unreachable endpoint needs no container). Accordingly this class is intentionally NOT part of the
    /// shared <c>"Integration"</c> collection.
    /// </para>
    ///
    /// <para>
    /// Naming follows the repository convention <c>MethodName_StateUnderTest_ExpectedBehavior</c>, with an
    /// Arrange-Act-Assert structure and FluentAssertions.
    /// </para>
    /// </summary>
    public class StartupMigrationIsolationTests
    {
        /// <summary>
        /// Well-formed Npgsql connection string that fails <b>fast</b> at connect time. Port 1 on the
        /// loopback (<c>127.0.0.1:1</c>) has nothing listening, so the connection is refused immediately;
        /// <c>Timeout=1</c> / <c>Command Timeout=1</c> cap any wait at ~1 second. This makes
        /// <c>MigrateAsync</c> genuinely throw with no hang and no <c>Thread.Sleep</c>.
        /// </summary>
        private const string UnreachableConnectionString =
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1";

        /// <summary>The exact message the production <c>Program.Main</c> catch logs on migration failure.</summary>
        private const string ProductionMigrationErrorMessage = "An error occured during migration";

        /// <summary>
        /// Launches the genuine compiled production host (<c>API.dll</c> via <c>Program.Main</c>) with
        /// unreachable database connection strings, then asserts the failure is isolated (logged by the
        /// real entry point) and the host still starts and listens — proving fail-closed continuity of the
        /// production startup path, not of any test-local copy.
        /// </summary>
        [Fact]
        public async Task Main_WhenStartupMigrationFailsAgainstUnreachableDatabase_LogsErrorViaRealEntryPointAndHostContinues()
        {
            // Arrange — resolve the compiled entry point and a disposable, isolated content root.
            var apiDll = ResolveApiAssemblyPath();
            File.Exists(apiDll).Should().BeTrue(
                $"the production API build output must exist to exercise the real entry point (looked for '{apiDll}'); " +
                "it is produced automatically when API.IntegrationTests is built because it references the API project");

            var muxer = ResolveDotnetMuxerPath();
            var port = GetFreeLoopbackPort();

            var tempRoot = Path.Combine(Path.GetTempPath(), "ecom-startupmig-" + Guid.NewGuid().ToString("N"));
            var homeDir = Path.Combine(tempRoot, "home");
            // Startup.Configure builds a PhysicalFileProvider over "{cwd}/Content" (which throws if the
            // directory is missing) and serves "{cwd}/wwwroot"; create both inside the disposable root so
            // no directory is ever created in the test process's own working directory.
            Directory.CreateDirectory(Path.Combine(tempRoot, "Content"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "wwwroot"));
            Directory.CreateDirectory(homeDir);

            var startInfo = new ProcessStartInfo
            {
                FileName = muxer,
                WorkingDirectory = tempRoot,       // content root -> the disposable temp tree
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(apiDll);

            // ProcessStartInfo.Environment is pre-seeded from the current process; override only the keys
            // that force a genuine migration failure and isolate all machine-global state.
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";                    // no dependency on appsettings.Development.json
            startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";              // HTTP-only ephemeral bind (no dev cert needed)
            startInfo.Environment["ConnectionStrings__DefaultConnection"] = UnreachableConnectionString;
            startInfo.Environment["ConnectionStrings__IdentityConnection"] = UnreachableConnectionString;
            startInfo.Environment["ConnectionStrings__Redis"] = "127.0.0.1:1";                  // not resolved at startup; unused by the migrate block
            startInfo.Environment["Token__Key"] = "super secret key which is long enough for offline startup only";
            startInfo.Environment["Token__Issuer"] = "https://localhost:5001";
            startInfo.Environment["ApiUrl"] = "https://localhost:5001/content/";
            startInfo.Environment["HOME"] = homeDir;                                            // isolate Data Protection key ring under the temp tree
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";

            var output = new StringBuilder();
            var sync = new object();
            void Capture(string data)
            {
                if (data == null) return;
                lock (sync) output.AppendLine(data);
            }
            string Snapshot()
            {
                lock (sync) return output.ToString();
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);

            try
            {
                // Act — start the REAL production host out-of-process.
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Readiness wait strategy: poll (bounded) until the production migration error has been
                // logged AND the host is accepting TCP connections, or until the process exits, or timeout.
                var timeout = TimeSpan.FromSeconds(120);
                var stopwatch = Stopwatch.StartNew();
                var loggedMigrationError = false;
                var isListening = false;
                var exitedEarly = false;

                while (stopwatch.Elapsed < timeout)
                {
                    loggedMigrationError = Snapshot().Contains(ProductionMigrationErrorMessage);
                    if (!isListening)
                    {
                        isListening = CanConnect(port);
                    }

                    if (process.HasExited)
                    {
                        exitedEarly = true;
                        break;
                    }

                    if (loggedMigrationError && isListening)
                    {
                        break;
                    }

                    await Task.Delay(250);
                }

                var captured = Snapshot();
                var diagnostic = "Captured child-process output (tail):\n" + Tail(captured, 3000);

                // Assert — the single conceptual outcome: the REAL Program.Main isolates a genuine startup
                // migration failure (logs it) and the host CONTINUES to listen instead of crashing.

                // Continuity: the production host must not crash the process on an isolated migration failure.
                exitedEarly.Should().BeFalse(
                    "an isolated startup migration failure must NOT terminate the production host process. " + diagnostic);
                process.HasExited.Should().BeFalse(
                    "the production host must keep running (fail-closed continuity to host.Run()). " + diagnostic);

                // Isolation + logging: the real production catch logged the migration failure.
                captured.Should().Contain(ProductionMigrationErrorMessage,
                    "Program.Main's catch must log the migration failure. " + diagnostic);

                // Genuine entry point: the logged exception's stack trace must originate from the REAL
                // production Program.Main (this is the assertion the copied-block approach could not make).
                captured.Should().Contain("API.Program.Main",
                    "the logged exception stack trace must originate from the real production entry point, " +
                    "proving Program.Main itself ran the migrate/seed block. " + diagnostic);

                // The isolated failure is genuinely a database connection failure (the intended trigger).
                captured.Should().Contain("Npgsql",
                    "the isolated failure must be a genuine PostgreSQL/Npgsql connection failure. " + diagnostic);

                // Continuity (positive signal): the host reached host.Run() and is accepting connections.
                isListening.Should().BeTrue(
                    $"the production host must be listening on 127.0.0.1:{port}, proving it reached host.Run() " +
                    "despite the migration failure. " + diagnostic);
            }
            finally
            {
                // Terminate exactly the process we spawned (and any children) by pid — never a broad signal.
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(15000);
                    }
                }
                catch
                {
                    // best-effort teardown; the OS reclaims the pid regardless
                }
                finally
                {
                    process.Dispose();
                }

                // Delete the disposable content root + isolated Data Protection key ring.
                TryDeleteDirectory(tempRoot);
            }
        }

        /// <summary>
        /// Resolves the absolute path to the compiled production <c>API.dll</c> from the running test
        /// assembly's location. The test executes from
        /// <c>{repo}/API.IntegrationTests/bin/{config}/{tfm}/</c>; the production host is built (via the
        /// project reference) to <c>{repo}/API/bin/{config}/{tfm}/API.dll</c> in the SAME configuration,
        /// so the configuration and target-framework folder names are read from the test's own base
        /// directory to track Debug vs Release automatically.
        /// </summary>
        private static string ResolveApiAssemblyPath()
        {
            var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var tfm = baseDir.Name;                 // e.g. net5.0
            var config = baseDir.Parent?.Name;      // e.g. Release or Debug

            // Walk up to the API.IntegrationTests project directory; its parent is the repository root.
            var dir = baseDir;
            while (dir != null && !string.Equals(dir.Name, "API.IntegrationTests", StringComparison.Ordinal))
            {
                dir = dir.Parent;
            }

            var repoRoot = dir?.Parent;
            if (repoRoot == null || config == null)
            {
                // Fallback: assume the standard bin/{config}/{tfm} layout four levels above the base dir.
                repoRoot = baseDir.Parent?.Parent?.Parent?.Parent;
                config ??= "Debug";
            }

            return Path.Combine(repoRoot!.FullName, "API", "bin", config, tfm, "API.dll");
        }

        /// <summary>
        /// Resolves the absolute path to the <c>dotnet</c> muxer used to launch the framework-dependent
        /// <c>API.dll</c>. Derives the .NET install root from the loaded shared framework directory (robust
        /// and independent of environment variables), with <c>DOTNET_ROOT</c> and a bare <c>"dotnet"</c>
        /// (PATH lookup) as fallbacks.
        /// </summary>
        private static string ResolveDotnetMuxerPath()
        {
            var muxerName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";

            // e.g. /usr/share/dotnet/shared/Microsoft.NETCore.App/5.0.17/ -> up 3 -> /usr/share/dotnet
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var dotnetRoot = new DirectoryInfo(runtimeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Parent?.Parent?.Parent?.FullName;
            if (dotnetRoot != null)
            {
                var candidate = Path.Combine(dotnetRoot, muxerName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(envRoot))
            {
                var candidate = Path.Combine(envRoot, muxerName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Last resort: rely on PATH resolution.
            return muxerName;
        }

        /// <summary>
        /// Reserves and immediately releases an ephemeral loopback TCP port so the child host can bind it.
        /// </summary>
        private static int GetFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Returns <c>true</c> if a TCP connection to the loopback <paramref name="port"/> succeeds within
        /// a short bound, indicating the host is listening. Any failure returns <c>false</c> so the caller
        /// keeps polling.
        /// </summary>
        private static bool CanConnect(int port)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                return connectTask.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns the trailing <paramref name="maxChars"/> characters of <paramref name="text"/>.</summary>
        private static string Tail(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            return "..." + text.Substring(text.Length - maxChars);
        }

        /// <summary>Best-effort recursive delete of the disposable content/key-ring tree.</summary>
        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // The directory lives under the OS temp path and is never committed; ignore teardown races.
            }
        }
    }
}
