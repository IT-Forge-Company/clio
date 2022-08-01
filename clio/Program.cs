using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autofac;
using Clio.Command;
using Clio.Command.PackageCommand;
using Clio.Command.SqlScriptCommand;
using Clio.Command.SysSettingsCommand;
using Clio.Command.UpdateCliCommand;
using Clio.Common;
using Clio.Package;
using Clio.Project;
using Clio.Querry;
using Clio.UserEnvironment;
using CommandLine;
using Creatio.Client;
using Newtonsoft.Json;
using Сlio.Command.PackageCommand;

namespace Clio
{


	class Program
	{
		private static string UserName => CreatioEnvironment.Settings.Login;
		private static string UserPassword => CreatioEnvironment.Settings.Password;
		private static string Url => CreatioEnvironment.Settings.Uri; // Необходимо получить из конфига
		private static string ClientId => CreatioEnvironment.Settings.ClientId;
		private static string ClientSecret => CreatioEnvironment.Settings.ClientSecret;
		private static string AuthAppUrl => CreatioEnvironment.Settings.AuthAppUri;
		private static string AppUrl
		{
			get
			{
				if (CreatioEnvironment.IsNetCore) {
					return Url;
				} else {
					return Url + @"/0";
				}
			}
		}

		private static string GetZipPackageUrl => AppUrl + @"/ServiceModel/PackageInstallerService.svc/GetZipPackages";

		private static string ApiVersionUrl => AppUrl + @"/rest/CreatioApiGateway/GetApiVersion";


		private static string GetEntityModelsUrl => AppUrl + @"/rest/CreatioApiGateway/GetEntitySchemaModels/{0}/{1}";

		private static CreatioClient _creatioClientInstance
		{
			get {
				if (string.IsNullOrEmpty(ClientId)) {
					return new CreatioClient(Url, UserName, UserPassword, true, CreatioEnvironment.IsNetCore);
				}
				else {
					return CreatioClient.CreateOAuth20Client(Url, AuthAppUrl, ClientId, ClientSecret, CreatioEnvironment.IsNetCore);
				}
			}
		}

		public static bool Safe { get; private set; } = true;

		private static void Configure(EnvironmentOptions options) {
			var settingsRepository = new SettingsRepository();
			CreatioEnvironment.EnvironmentName = options.Environment;
			CreatioEnvironment.Settings = settingsRepository.GetEnvironment(options);
			ICreatioEnvironment creatioEnvironment = Resolve<ICreatioEnvironment>();
		}

		private static void MessageToConsole(string text, ConsoleColor color) {
			var currentColor = Console.ForegroundColor;
			Console.ForegroundColor = color;
			Console.WriteLine(text);
			Console.ForegroundColor = currentColor;
		}


		public static void SetupAppConnection(EnvironmentOptions options) {
			Configure(options);
			CheckApiVersion();
		}


		public static void CheckApiVersion() {
			var dir = AppDomain.CurrentDomain.BaseDirectory;
			string versionFilePath = Path.Combine(dir, "cliogate", "version.txt");
			var localApiVersion = new Version(File.ReadAllText(versionFilePath));
			var appApiVersion = GetAppApiVersion();
			if (appApiVersion == new Version("0.0.0.0")) {
				MessageToConsole($"Your app does not contain clio API." +
				 $"{Environment.NewLine}You should consider install it via the \'clio install-gate\' command.", ConsoleColor.DarkYellow);
			} else if (localApiVersion > appApiVersion) {
				MessageToConsole($"You are using clio api version {appApiVersion}, however version {localApiVersion} is available." +
				 $"{Environment.NewLine}You should consider upgrading via the \'clio update-gate\' command.", ConsoleColor.DarkYellow);
			}
		}


		private static Version GetAppApiVersion() {
			var apiVersion = new Version("0.0.0.0");
			try {
				string appVersionResponse = _creatioClientInstance.ExecuteGetRequest(ApiVersionUrl).Trim('"');
				apiVersion = new Version(appVersionResponse);
			} catch (Exception) {
			}
			return apiVersion;
		}

		private static void DownloadZipPackagesInternal(string packageName, string destinationPath) {
			try {
				Console.WriteLine("Start download packages ({0}).", packageName);
				var packageNames = string.Format("\"{0}\"", packageName.Replace(" ", string.Empty).Replace(",", "\",\""));
				string requestData = "[" + packageNames + "]";
				_creatioClientInstance.DownloadFile(GetZipPackageUrl, destinationPath, requestData);
				Console.WriteLine("Download packages ({0}) completed.", packageName);
			} catch (Exception) {
				Console.WriteLine("Download packages ({0}) not completed.", packageName);
			}
		}


		private static string CorrectJson(string body) {
			body = body.Replace("\\\\r\\\\n", Environment.NewLine);
			body = body.Replace("\\r\\n", Environment.NewLine);
			body = body.Replace("\\\\n", Environment.NewLine);
			body = body.Replace("\\n", Environment.NewLine);
			body = body.Replace("\\\\t", Convert.ToChar(9).ToString());
			body = body.Replace("\\\"", "\"");
			body = body.Replace("\\\\", "\\");
			body = body.Trim(new Char[] { '\"' });
			return body;
		}

		private static void UnZipPackages(string zipFilePath) {
			IPackageArchiver packageArchiver = Resolve<IPackageArchiver>();
			packageArchiver.ExtractPackages(zipFilePath, true, true, false, "");
		}

		private static void UnZip(string zipFilePath) {
			IPackageArchiver packageArchiver = Resolve<IPackageArchiver>();
			packageArchiver.UnZip(zipFilePath, true, "");
		}

		private static int DownloadZipPackages(PullPkgOptions options) {
			try {
				SetupAppConnection(options);
				string destPath = options.DestPath ?? Path.Combine(Path.GetTempPath(), "packages.zip");
				DownloadZipPackagesInternal(options.Name, destPath);
				if (options.Unzip) {
					UnZipPackages(destPath);
				} else {
					UnZip(destPath);
				}
				Console.WriteLine("Done");
				return 0;
			} catch (Exception e) {
				Console.WriteLine(e);
				return 1;
			}
		}

		private static EnvironmentSettings GetEnvironmentSettings(EnvironmentOptions options) {
			var settingsRepository = new SettingsRepository();
			return settingsRepository.GetEnvironment(options);
		}

		private static TCommand CreateRemoteCommand<TCommand>(EnvironmentOptions options,
				params object[] additionalConstructorArgs) {
			var settings = GetEnvironmentSettings(options);
			var creatioClient = string.IsNullOrEmpty(settings.ClientId) ? new CreatioClient(settings.Uri, settings.Login, settings.Password, true, settings.IsNetCore) :
					CreatioClient.CreateOAuth20Client(settings.Uri, settings.AuthAppUri, settings.ClientId, settings.ClientSecret, settings.IsNetCore);
			var clientAdapter = new CreatioClientAdapter(creatioClient);
			var constructorArgs = new object[] { clientAdapter, settings }.Concat(additionalConstructorArgs).ToArray();
			return (TCommand)Activator.CreateInstance(typeof(TCommand), constructorArgs);
		}

		//ToDo: move to factory
		private static TCommand CreateCommand<TCommand>(params object[] additionalConstructorArgs) {
			return (TCommand)Activator.CreateInstance(typeof(TCommand), additionalConstructorArgs);
		}

		private static PushPkgOptions CreatePushPkgOptions(InstallGateOptions options) {
			var settingsRepository = new SettingsRepository();
			var settings = settingsRepository.GetEnvironment(options);
			var workingDirectoriesProvider = Resolve<IWorkingDirectoriesProvider>(options);
			string packageName = settings.IsNetCore ? "cliogate_netcore" : "cliogate";
			string packagePath = Path.Combine(workingDirectoriesProvider.ExecutingDirectory, "cliogate",
				$"{packageName}.gz");
			return new PushPkgOptions {
				Environment = options.Environment,
				Name = packagePath,
				Login = options.Login,
				Uri = options.Uri,
				Password = options.Password,
				Maintainer = options.Maintainer,
				IsNetCore = options.IsNetCore
			};
		}

		private static T Resolve<T>(EnvironmentOptions options = null) {
			EnvironmentSettings settings = null; 
			if (options != null) {
				settings = GetEnvironmentSettings(options);
			}
			var container = new BindingsModule().Register(settings);
			return container.Resolve<T>();
		}


		private static int Main(string[] args) {
			var autoupdate = new SettingsRepository().GetAutoupdate();
			if (autoupdate) {
				new Thread(UpdateCliCommand.CheckUpdate).Start();
			}
			var creatioEnv = new CreatioEnvironment();
			string helpFolderName = $"help";
			string helpDirectoryPath = helpFolderName;
			var envPath = creatioEnv.GetAssemblyFolderPath();
			helpDirectoryPath = Path.Combine(envPath ?? string.Empty, helpFolderName);
			Parser.Default.Settings.ShowHeader = false;
			Parser.Default.Settings.HelpDirectory = helpDirectoryPath;
			Parser.Default.Settings.CustomHelpViewer = new WikiHelpViewer();
			return Parser.Default.ParseArguments<ExecuteAssemblyOptions, RestartOptions, ClearRedisOptions,
					RegAppOptions, AppListOptions, UnregAppOptions, GeneratePkgZipOptions, PushPkgOptions,
					DeletePkgOptions, ReferenceOptions, NewPkgOptions, ConvertOptions, RegisterOptions,
					UnregisterOptions, PullPkgOptions, ExecuteSqlScriptOptions, InstallGateOptions, ItemOptions,
					DeveloperModeOptions, SysSettingsOptions, FeatureOptions, UnzipPkgOptions, PingAppOptions,
					OpenAppOptions, PkgListOptions, CompileOptions, PushNuGetPkgsOptions, PackNuGetPkgOptions,
					RestoreNugetPkgOptions, InstallNugetPkgOptions, SetPackageVersionOptions, GetPackageVersionOptions, 
					CheckNugetUpdateOptions, RestoreWorkspaceOptions, CreateWorkspaceCommandOptions, PushWorkspaceCommandOptions,
					LoadPackagesToFileSystemOptions, UploadLicensesOptions, LoadPackagesToDbOptions, HealthCheckOptions,
					AddPackageOptions, UnlockPackageOptions, LockPackageOptions, DataServiceQuerryOptions, 
					RestoreFromPackageBackupOptions, GetMarketplaceCatalogOptions>(args)
				.MapResult(
					(ExecuteAssemblyOptions opts) => CreateRemoteCommand<AssemblyCommand>(opts).Execute(opts),
					(RestartOptions opts) => CreateRemoteCommand<RestartCommand>(opts).Execute(opts),
					(ClearRedisOptions opts) => CreateRemoteCommand<RedisCommand>(opts).Execute(opts),
					(RegAppOptions opts) => CreateCommand<RegAppCommand>(
						new SettingsRepository(), new ApplicationClientFactory()).Execute(opts),
					(AppListOptions opts) => CreateCommand<ShowAppListCommand>(new SettingsRepository()).Execute(opts),
					(UnregAppOptions opts) => CreateCommand<UnregAppCommand>(new SettingsRepository()).Execute(opts),
					(GeneratePkgZipOptions opts) => Resolve<CompressPackageCommand>().Execute(opts),
					(PushPkgOptions opts) => Resolve<PushPackageCommand>(opts).Execute(opts),
					(DeletePkgOptions opts) => Resolve<DeletePackageCommand>(opts).Execute(opts),
					(ReferenceOptions opts) => CreateCommand<ReferenceCommand>(new CreatioPkgProjectCreator()).Execute(opts),
					(NewPkgOptions opts) => CreateCommand<NewPkgCommand>(new SettingsRepository(), CreateCommand<ReferenceCommand>(
						new CreatioPkgProjectCreator())).Execute(opts),
					(ConvertOptions opts) => ConvertPackage(opts),
					(RegisterOptions opts) => CreateCommand<RegisterCommand>().Execute(opts),
					(UnregisterOptions opts) => CreateCommand<UnregisterCommand>().Execute(opts),
					(PullPkgOptions opts) => DownloadZipPackages(opts),
					(ExecuteSqlScriptOptions opts) => Resolve<SqlScriptCommand>(opts).Execute(opts),
					(InstallGateOptions opts) => Resolve<PushPackageCommand>(CreatePushPkgOptions(opts))
						.Execute(CreatePushPkgOptions(opts)),
					(ItemOptions opts) => AddItem(opts),
					(DeveloperModeOptions opts) => SetDeveloperMode(opts),
					(SysSettingsOptions opts) => CreateRemoteCommand<SysSettingsCommand>(opts).Execute(opts),
					(FeatureOptions opts) => CreateRemoteCommand<FeatureCommand>(opts).Execute(opts),
					(UnzipPkgOptions opts) => ExtractPackageCommand.ExtractPackage(opts,
						Resolve<IPackageArchiver>(),Resolve<IPackageUtilities>(),
						Resolve<IFileSystem>()),
					(PingAppOptions opts) => CreateRemoteCommand<PingAppCommand>(opts).Execute(opts),
					(OpenAppOptions opts) => CreateRemoteCommand<OpenAppCommand>(opts).Execute(opts),
					(PkgListOptions opts) => Resolve<GetPkgListCommand>(opts).Execute(opts),
					(CompileOptions opts) => CreateRemoteCommand<CompileWorkspaceCommand>(opts).Execute(opts),
					(PushNuGetPkgsOptions opts) => Resolve<PushNuGetPackagesCommand>(opts).Execute(opts),
					(PackNuGetPkgOptions opts) => Resolve<PackNuGetPackageCommand>(opts).Execute(opts),
					(RestoreNugetPkgOptions opts) => Resolve<RestoreNugetPackageCommand>(opts).Execute(opts),
					(InstallNugetPkgOptions opts) => Resolve<InstallNugetPackageCommand>(opts).Execute(opts),
					(SetPackageVersionOptions opts) => Resolve<SetPackageVersionCommand>().Execute(opts),
					(GetPackageVersionOptions opts) => Resolve<GetPackageVersionCommand>().Execute(opts),
					(CheckNugetUpdateOptions opts) => Resolve<CheckNugetUpdateCommand>(opts).Execute(opts),
					(RestoreWorkspaceOptions opts) => Resolve<RestoreWorkspaceCommand>(opts).Execute(opts),
					(CreateWorkspaceCommandOptions opts) => Resolve<CreateWorkspaceCommand>(opts).Execute(opts),
					(PushWorkspaceCommandOptions opts) => Resolve<PushWorkspaceCommand>(opts).Execute(opts),
					//(UploadLicenseCommandOptions opts) => Resolve<UploadLicenseCommand>(opts).Execute(opts),
					(LoadPackagesToFileSystemOptions opts) => Resolve<LoadPackagesToFileSystemCommand>(opts)
						.Execute(opts),
					(LoadPackagesToDbOptions opts) => Resolve<LoadPackagesToDbCommand>(opts).Execute(opts),
					(UploadLicensesOptions opts) => Resolve<UploadLicensesCommand>(opts).Execute(opts),
					(HealthCheckOptions opts) => Resolve<HealthCheckCommand>(opts).Execute(opts),
					(AddPackageOptions opts) => Resolve<AddPackageCommand>(opts).Execute(opts),
					(UnlockPackageOptions opts) => Resolve<UnlockPackageCommand>(opts).Execute(opts),
					(LockPackageOptions opts) => Resolve<LockPackageCommand>(opts).Execute(opts),
					(DataServiceQuerryOptions opts) => Resolve<DataServiceQuerry>(opts).Execute(opts),
					(RestoreFromPackageBackupOptions opts) => Resolve<RestoreFromPackageBackupCommand>(opts).Execute(opts),
					(GetMarketplaceCatalogOptions opts) => Resolve<GetMarketplacecatalogCommand>(opts).Execute(opts),
					errs => 1);
					
		}

		private static int SetDeveloperMode(DeveloperModeOptions opts) {
			try {
				SetupAppConnection(opts);
				var repository = new SettingsRepository();
				CreatioEnvironment.Settings.DeveloperModeEnabled = true;
				repository.ConfigureEnvironment(CreatioEnvironment.EnvironmentName, CreatioEnvironment.Settings);
				var sysSettingOptions = new SysSettingsOptions() {
					Code = "Maintainer",
					Value = CreatioEnvironment.Settings.Maintainer
				};
				var sysSettingsCommand = CreateRemoteCommand<SysSettingsCommand>(sysSettingOptions);
				sysSettingsCommand.UpdateSysSetting(sysSettingOptions, CreatioEnvironment.Settings);
				UnlockMaintainerPackageInternal(opts);
				new RestartCommand(new CreatioClientAdapter(_creatioClientInstance), CreatioEnvironment.Settings).Execute(new RestartOptions());
				Console.WriteLine("Done");
				return 0;
			} catch (Exception e) {
				Console.WriteLine(e);
				return 1;
			}
		}

		private static void UnlockMaintainerPackageInternal(EnvironmentOptions environmentOptions) {
			IPackageLockManager packageLockManager = Resolve<IPackageLockManager>(environmentOptions);
			packageLockManager.Unlock();
		}

		private static int AddModels(ItemOptions opts) {
			try {
				SetupAppConnection(opts);
				var models = GetClassModels(opts.ItemName, opts.Fields);
				var project = new VSProject(opts.DestinationPath, opts.Namespace);
				foreach (var model in models) {
					project.AddFile(model.Key, model.Value);
				}
				project.Reload();
				Console.WriteLine("Done");
				return 0;
			} catch (Exception e) {
				Console.WriteLine(e);
				return 1;
			}
		}

		private static int AddItem(ItemOptions options) {
			if (options.ItemType.ToLower() == "model") {
				return AddModels(options);
			} else {
				return AddItemFromTemplate(options);
			}
		}

		private static int AddItemFromTemplate(ItemOptions options) {
			try {
				var project = new VSProject(options.DestinationPath, options.Namespace);
				var creatioEnv = new CreatioEnvironment();
				string tplPath = $"tpl{Path.DirectorySeparatorChar}{options.ItemType}-template.tpl";
				if (!File.Exists(tplPath)) {
					var envPath = creatioEnv.GetAssemblyFolderPath();
					if (!string.IsNullOrEmpty(envPath)) {
						tplPath = Path.Combine(envPath, tplPath);
					}
				}
				string templateBody = File.ReadAllText(tplPath);
				project.AddFile(options.ItemName, templateBody.Replace("<Name>", options.ItemName));
				project.Reload();
				return 0;
			} catch (Exception e) {
				Console.WriteLine(e);
				return 1;
			}
		}

		private static Dictionary<string, string> GetClassModels(string entitySchemaName, string fields) {
			var url = string.Format(GetEntityModelsUrl, entitySchemaName, fields);
			string responseFormServer = _creatioClientInstance.ExecuteGetRequest(url);
			var result = CorrectJson(responseFormServer);
			return JsonConvert.DeserializeObject<Dictionary<string, string>>(result);
		}

		private static int ConvertPackage(ConvertOptions opts) {
			return PackageConverter.Convert(opts);
		}
	}
}
