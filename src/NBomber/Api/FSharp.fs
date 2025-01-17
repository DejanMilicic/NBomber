﻿namespace NBomber.FSharp

open System
open System.IO
open System.Threading.Tasks

open Serilog
open CommandLine
open FSharp.Control.Tasks.NonAffine
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Configuration

open NBomber
open NBomber.Contracts
open NBomber.Configuration
open NBomber.Errors
open NBomber.Domain
open NBomber.Domain.DomainTypes
open NBomber.Domain.ConnectionPool
open NBomber.DomainServices

type CommandLineArgs = {
    [<Option('c', "config", HelpText = "NBomber configuration")>] Config: string
    [<Option('i', "infra", HelpText = "NBomber infra configuration")>] InfraConfig: string
}

[<AutoOpen>]
module TimeSpanApi =

    let inline milliseconds (value: int) = value |> float |> TimeSpan.FromMilliseconds
    let inline seconds (value: int) = value |> float |> TimeSpan.FromSeconds
    let inline minutes (value) = value |> float |> TimeSpan.FromMinutes

type ConnectionPoolArgs =

    static member internal create (name: string,
                                   openConnection: int * IBaseContext -> Task<'TConnection>,
                                   closeConnection: 'TConnection * IBaseContext -> Task,
                                   ?connectionCount: int) =

        let count = defaultArg connectionCount Constants.DefaultConnectionCount
        ConnectionPoolArgs(name, count, openConnection, closeConnection)
        :> IConnectionPoolArgs<'TConnection>

    static member create (name: string,
                          openConnection: int * IBaseContext -> Task<'TConnection>,
                          closeConnection: 'TConnection * IBaseContext -> Task<unit>,
                          ?connectionCount: int) =

        let close = fun (connection,token) -> closeConnection(connection,token) :> Task
        let count = defaultArg connectionCount Constants.DefaultConnectionCount
        ConnectionPoolArgs.create(name, openConnection, close, count)

type Step =

    static member internal create (name: string,
                                  execute: IStepContext<'TConnection,'TFeedItem> -> Task<Response>,
                                  connectionPoolArgs: IConnectionPoolArgs<'TConnection> option,
                                  feed: IFeed<'TFeedItem> option,
                                  doNotTrack: bool option) =
        let poolArgs =
            connectionPoolArgs
            |> Option.map(fun x -> x :?> ConnectionPoolArgs<'TConnection>)
            |> Option.map(fun x -> x.GetUntyped().Value)

        { StepName = name
          ConnectionPoolArgs = poolArgs
          ConnectionPool = None
          Execute = Step.toUntypedExec execute
          Feed = feed |> Option.map Feed.toUntypedFeed
          DoNotTrack = defaultArg doNotTrack Constants.DefaultDoNotTrack }
          :> IStep

    static member create (name: string,
                          connectionPoolArgs: IConnectionPoolArgs<'TConnection>,
                          feed: IFeed<'TFeedItem>,
                          execute: IStepContext<'TConnection,'TFeedItem> -> Task<Response>,
                          ?doNotTrack: bool) =
        Step.create(name, execute, Some connectionPoolArgs, Some feed, doNotTrack)

    static member create (name: string,
                          connectionPoolArgs: IConnectionPoolArgs<'TConnection>,
                          execute: IStepContext<'TConnection,unit> -> Task<Response>,
                          ?doNotTrack: bool) =
        Step.create(name, execute, Some connectionPoolArgs, None, doNotTrack)

    static member create (name: string,
                          feed: IFeed<'TFeedItem>,
                          execute: IStepContext<unit,'TFeedItem> -> Task<Response>,
                          ?doNotTrack: bool) =
        Step.create(name, execute, None, Some feed, doNotTrack)

    static member create (name: string,
                          execute: IStepContext<unit,unit> -> Task<Response>,
                          ?doNotTrack: bool) =
        Step.create(name, execute, None, None, doNotTrack)

    /// Creates pause step with specified duration in lazy mode.
    /// It's useful when you want to fetch value from some configuration.
    static member createPause (getDuration: unit -> TimeSpan) =
        Step.create(name = "pause",
                    execute = (fun _ -> task { do! Task.Delay(getDuration())
                                               return Response.Ok() }),
                    doNotTrack = true)

    /// Creates pause step in milliseconds in lazy mode.
    /// It's useful when you want to fetch value from some configuration.
    static member createPause (getDuration: unit -> int) =
        let func = getDuration >> float >> TimeSpan.FromMilliseconds
        Step.createPause(func)

    /// Creates pause step with specified duration.
    static member createPause (duration: TimeSpan) =
        Step.createPause(fun () -> duration)

    /// Creates pause step with specified duration in milliseconds.
    static member createPause (milliseconds: int) =
        Step.createPause(fun () -> milliseconds)

/// Scenario helps to organize steps into sequential flow with different load simulations (concurrency control).
module Scenario =

    /// Creates scenario with steps which will be executed sequentially.
    let create (name: string) (steps: IStep list): Contracts.Scenario =
        let stepsOrder = [|0..steps.Length-1|]
        { ScenarioName = name
          Init = None
          Clean = None
          Steps = steps
          WarmUpDuration = Constants.DefaultWarmUpDuration
          LoadSimulations = [LoadSimulation.InjectPerSec(rate = Constants.DefaultCopiesCount, during = Constants.DefaultSimulationDuration)]
          GetStepsOrder = fun () -> stepsOrder }

    /// Initializes scenario.
    /// You can use it to for example to prepare your target system or to parse and apply configuration.
    let withInit (initFunc: IScenarioContext -> Task<unit>) (scenario: Contracts.Scenario) =
        { scenario with Init = Some(fun token -> initFunc(token) :> Task) }

    /// Cleans scenario's resources.
    let withClean (cleanFunc: IScenarioContext -> Task<unit>) (scenario: Contracts.Scenario) =
        { scenario with Clean = Some(fun token -> cleanFunc(token) :> Task) }

    /// Sets warm-up duration
    /// Warm-up will just simply start a scenario with a specified duration.
    let withWarmUpDuration (duration: TimeSpan) (scenario: Contracts.Scenario) =
        { scenario with WarmUpDuration = duration }

    let withoutWarmUp (scenario: Contracts.Scenario) =
        { scenario with WarmUpDuration = TimeSpan.Zero }

    /// Sets load simulations.
    /// Default value is: InjectPerSec(rate = 50, during = minutes 1)
    let withLoadSimulations (loadSimulations: LoadSimulation list) (scenario: Contracts.Scenario) =
        { scenario with LoadSimulations = loadSimulations }

    /// Sets custom steps order that will be used by NBomber Scenario executor.
    /// By default, all steps are executing sequentially but you can inject your custom order.
    /// getStepsOrder function will be invoked on every turn before steps list execution.
    let withCustomStepsOrder (getStepsOrder: unit -> int[]) (scenario: Contracts.Scenario) =
        { scenario with GetStepsOrder = getStepsOrder }

/// NBomberRunner is responsible for registering and running scenarios.
/// Also it provides configuration points related to infrastructure, reporting, loading plugins.
module NBomberRunner =

    /// Registers scenario in NBomber environment.
    let registerScenario (scenario: Contracts.Scenario) =
        { NBomberContext.empty with RegisteredScenarios = [scenario] }

    /// Registers scenarios in NBomber environment.
    /// Scenarios will be run in parallel.
    let registerScenarios (scenarios: Contracts.Scenario list) =
        { NBomberContext.empty with RegisteredScenarios = scenarios }

    /// Sets test suite name
    /// Default value is: nbomber_default_test_suite_name.
    let withTestSuite (testSuite: string) (context: NBomberContext) =
        { context with TestSuite = testSuite }

    /// Sets test name
    /// Default value is: nbomber_default_test_name.
    let withTestName (testName: string) (context: NBomberContext) =
        { context with TestName = testName }

    /// Sets output report name.
    /// Default name: nbomber_report.
    let withReportFileName (reportFileName: string) (context: NBomberContext) =
        let report = { context.Reporting with FileName = Some reportFileName }
        { context with Reporting = report }

    /// Sets output report folder path.
    /// Default folder path: "./reports".
    let withReportFolder (reportFolderPath: string) (context: NBomberContext) =
        let report = { context.Reporting with FolderName = Some reportFolderPath }
        { context with Reporting = report }

    let withReportFormats (reportFormats: ReportFormat list) (context: NBomberContext) =
        let report = { context.Reporting with Formats = reportFormats }
        { context with Reporting  = report }

    /// Sets to run without reports
    let withoutReports (context: NBomberContext) =
        let report = { context.Reporting with Formats = [] }
        { context with Reporting = report }

    /// Sets reporting sinks.
    /// Reporting sink is used to save real-time metrics to correspond database
    let withReportingSinks (reportingSinks: IReportingSink list) (sendStatsInterval: TimeSpan) (context: NBomberContext) =
        let report = { context.Reporting with Sinks = reportingSinks
                                              SendStatsInterval = sendStatsInterval }
        { context with Reporting = report }

    /// Sets worker plugins.
    /// Worker plugin is a plugin that starts at the test start and works as a background worker.
    let withWorkerPlugins (plugins: IWorkerPlugin list) (context: NBomberContext) =
        { context with WorkerPlugins = plugins }

    /// Loads configuration.
    /// The following formats are supported:
    /// - json (.json)
    let loadConfig (path: string) (context: NBomberContext) =
        let config =
            match Path.GetExtension(path) with
            | ".json" -> path |> File.ReadAllText |> JsonConfig.unsafeParse
            | _       -> failwith "unsupported config format"

        { context with NBomberConfig = Some config }

    /// Loads infrastructure configuration.
    /// The following formats are supported:
    /// - json (.json)
    let loadInfraConfig (path: string) (context: NBomberContext) =
        let config =
            match Path.GetExtension(path) with
            | ".json" -> ConfigurationBuilder().AddJsonFile(path).Build() :> IConfiguration
            | _       -> failwith "unsupported config format"

        { context with InfraConfig = Some config }

    /// Sets logger configuration.
    /// Make sure that you always return a new instance of LoggerConfiguration.
    /// You can also configure logger via configuration file.
    /// For this use NBomberRunner.loadInfraConfig
    let withLoggerConfig (createLoggerConfig: unit -> LoggerConfiguration) (context: NBomberContext) =
        try
            // this is limitation of Serilog
            // to invoke CreateLogger() twice on the same instance of LoggerConfiguration
            // it's why we can't just accept LoggerConfiguration
            createLoggerConfig().CreateLogger() |> ignore
            createLoggerConfig().CreateLogger() |> ignore
        with
        | :? InvalidOperationException ->
            failwith "createLoggerConfig should always return a new instance of LoggerConfiguration"

        { context with CreateLoggerConfig = Some createLoggerConfig }

    /// Sets application type.
    /// The following application types are supported:
    /// - Console: is suitable for interactive session (will display progress bar)
    /// - Process: is suitable for running tests under test runners (progress bar will not be shown)
    /// By default NBomber will automatically identify your environment: Process or Console.
    let withApplicationType (applicationType: ApplicationType) (context: NBomberContext) =
        { context with ApplicationType = Some applicationType }

    /// Disables hints analyzer.
    /// Hints analyzer - analyze node stats to provide some hints in case of finding wrong usage or some other issue.
    let disableHintsAnalyzer (context: NBomberContext) =
        { context with UseHintsAnalyzer = false }

    let internal executeCliArgs (args) (context: NBomberContext) =
        let invokeConfigLoader (configName) (configLoader) (config) (context) =
            if config = String.Empty then sprintf "%s is empty" configName |> failwith
            elif String.IsNullOrEmpty(config) then context
            else configLoader config context

        match CommandLine.Parser.Default.ParseArguments<CommandLineArgs>(args) with
        | :? Parsed<CommandLineArgs> as parsed ->
            let values = parsed.Value
            let execLoadConfigCmd = invokeConfigLoader "config" loadConfig values.Config
            let execLoadInfraConfigCmd = invokeConfigLoader "infra config" loadInfraConfig values.InfraConfig
            let execCmd = execLoadConfigCmd >> execLoadInfraConfigCmd

            context |> execCmd

        | _ -> context

    let internal runWithResult (args) (context: NBomberContext) =
        context
        |> executeCliArgs args
        |> NBomberRunner.run

    let run (context: NBomberContext) =
        context
        |> runWithResult List.empty
        |> Result.mapError(AppError.toString)

    /// Runs scenarios with arguments.
    /// The following CLI commands are supported:
    /// -c or --config: loads configuration,
    /// -i or --infra: loads infrastructure configuration.
    /// Examples of possible args:
    /// -c config.json -i infra_config.json
    /// --config=config.json --infra=infra_config.json
    let runWithArgs (args) (context: NBomberContext) =
        context
        |> runWithResult args
        |> Result.mapError(AppError.toString)
