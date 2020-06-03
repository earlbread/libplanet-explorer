using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bencodex.Types;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blockchain.Policies;
using Libplanet.Blocks;
using Libplanet.Crypto;
using Libplanet.Explorer.Interfaces;
using Libplanet.Explorer.Store;
using Libplanet.Net;
using Libplanet.Store;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using NetMQ;
using Serilog;
using Serilog.Events;

namespace Libplanet.Explorer.Executable
{
    /// <summary>
    /// The program entry point to run a web server.
    /// </summary>
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Options options = Options.Parse(args, Console.Error);

            var loggerConfig = new LoggerConfiguration();
            loggerConfig = options.Debug
                ? loggerConfig.MinimumLevel.Debug()
                : loggerConfig.MinimumLevel.Information();
            loggerConfig = loggerConfig
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console();
            Log.Logger = loggerConfig.CreateLogger();

            bool readOnlyMode = options.Seeds is null;

            // Initialized DefaultStore.
            IStore store = new DefaultStore(
                path: options.StorePath,
                flush: false,
                readOnly: readOnlyMode
            );

            if (options.Seeds.Any())
            {
                // Warp up store.
                store = new RichStore(
                    store,
                    path: options.StorePath,
                    flush: false,
                    readOnly: readOnlyMode
                );
            }

            IBlockPolicy<AppAgnosticAction> policy = new BlockPolicy<AppAgnosticAction>(
                null,
                blockIntervalMilliseconds: options.BlockIntervalMilliseconds,
                minimumDifficulty: options.MinimumDifficulty,
                difficultyBoundDivisor: options.DifficultyBoundDivisor);
            var blockChain = new BlockChain<AppAgnosticAction>(policy, store, options.GenesisBlock);
            Startup.BlockChainSingleton = blockChain;
            Startup.StoreSingleton = store;

            IWebHost webHost = WebHost.CreateDefaultBuilder()
                .UseStartup<ExplorerStartup<AppAgnosticAction, Startup>>()
                .UseSerilog()
                .UseUrls($"http://{options.Host}:{options.Port}/")
                .Build();

            Swarm<AppAgnosticAction> swarm = null;
            if (options.Seeds.Any())
            {
                Console.WriteLine(
                    $"Seeds are {options.SeedStrings.Aggregate(string.Empty, (s, s1) => s + s1)}");

                // TODO: Take privateKey as a CLI option
                // TODO: Take appProtocolVersion as a CLI option
                // TODO: Take host as a CLI option
                // TODO: Take listenPort as a CLI option
                if (options.IceServer is null)
                {
                    Console.Error.WriteLine(
                        "error: -s/--seed option requires -I/--ice-server as well."
                    );
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine("Creating Swarm.");

                var privateKey = new PrivateKey();

                // FIXME: The appProtocolVersion should be fixed properly.
                swarm = new Swarm<AppAgnosticAction>(
                    blockChain,
                    privateKey,
                    options.AppProtocolVersionToken is string t
                        ? AppProtocolVersion.FromToken(t)
                        : default(AppProtocolVersion),
                    differentAppProtocolVersionEncountered: (p, pv, lv) => true,
                    iceServers: new[] { options.IceServer }
                );
            }

            using (var cts = new CancellationTokenSource())
            using (swarm)
            {
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cts.Cancel();
                };

                try
                {
                    await Task.WhenAll(
                        webHost.RunAsync(cts.Token),
                        StartSwarmAsync(swarm, options.Seeds, cts.Token)
                    );
                }
                catch (OperationCanceledException)
                {
                    await swarm?.StopAsync(waitFor: TimeSpan.FromSeconds(1))
                        .ContinueWith(_ => NetMQConfig.Cleanup(false));
                }
            }
        }

        private static async Task StartSwarmAsync(
            Swarm<AppAgnosticAction> swarm,
            IEnumerable<Peer> seeds,
            CancellationToken cancellationToken)
        {
            if (swarm is null)
            {
                return;
            }

            try
            {
                Console.WriteLine("Bootstrapping.");
                await swarm.BootstrapAsync(
                    seeds,
                    5000,
                    5000,
                    cancellationToken: cancellationToken
                );
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("No any neighbors.");
            }

            // Since explorer does not require states, turn off trustedPeer option.
            var trustedPeers = ImmutableHashSet<Address>.Empty;
            Console.WriteLine("Starts preloading.");
            await swarm.PreloadAsync(
                dialTimeout: TimeSpan.FromSeconds(15),
                trustedStateValidators: trustedPeers,
                cancellationToken: cancellationToken,
                blockDownloadFailed: (obj, args) =>
                {
                    foreach (var exception in args.InnerExceptions)
                    {
                        if (exception is InvalidGenesisBlockException invalidGenesisBlockException)
                        {
                            Log.Error(
                                "It seems you use different genesis block with the network. " +
                                "The hash stored was {Stored} but network was {Network}",
                                invalidGenesisBlockException.Stored.ToString(),
                                invalidGenesisBlockException.NetworkExpected.ToString());
                        }
                    }
                }
            );
            Console.WriteLine("Finished preloading.");

            await swarm.StartAsync(cancellationToken: cancellationToken);
        }

        internal class AppAgnosticAction : IAction
        {
            public IValue PlainValue
            {
                get;
                private set;
            }

            public void LoadPlainValue(
                IValue plainValue)
            {
                PlainValue = plainValue;
            }

            public IAccountStateDelta Execute(IActionContext context)
            {
                return context.PreviousStates;
            }

            public void Render(
                IActionContext context,
                IAccountStateDelta nextStates)
            {
            }

            public void RenderError(IActionContext context, Exception exception)
            {
            }

            public void Unrender(
                IActionContext context,
                IAccountStateDelta nextStates)
            {
            }

            public void UnrenderError(IActionContext context, Exception exception)
            {
            }
        }

        internal class Startup : IBlockChainContext<AppAgnosticAction>
        {
            public BlockChain<AppAgnosticAction> BlockChain => BlockChainSingleton;

            public IStore Store => StoreSingleton;

            internal static BlockChain<AppAgnosticAction> BlockChainSingleton { get; set; }

            internal static IStore StoreSingleton { get; set; }
        }
    }
}
