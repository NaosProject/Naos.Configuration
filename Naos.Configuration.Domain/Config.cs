// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Config.cs" company="Naos Project">
//    Copyright (c) Naos Project 2019. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace Naos.Configuration.Domain
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Security;
    using System.Security.Cryptography.Pkcs;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using OBeautifulCode.Collection.Recipes;
    using OBeautifulCode.Reflection.Recipes;
    using OBeautifulCode.Representation.System;
    using OBeautifulCode.Security.Recipes;
    using OBeautifulCode.Serialization;
    using OBeautifulCode.Serialization.Json;
    using OBeautifulCode.Type.Recipes;

    /// <summary>
    /// Config retrieval entry harness.
    /// </summary>
    public static class Config
    {
        private static readonly ConcurrentDictionary<Type, object> ResolvedSettings;
        private static readonly ConcurrentDictionary<string, object> ResolvedByNameSettings;
        private static readonly Lazy<IEnumerable<ISettingsSource>> DefaultSources;
        private static readonly object SyncUpdateSettings = new object();
        private static Func<string, SecureString> certificatePassword;
        private static IEnumerable<ISettingsSource> sources;
        private static Lazy<string[]> precedence;
        private static ISerializerFactory serializerFactory;
        private static SerializerRepresentation serializerRepresentation;

        /// <summary>
        /// Default shared precedence at the end.
        /// </summary>
        public const string CommonPrecedence = "Common";

        /// <summary>
        /// Default name of directory with config files in precedence folders.
        /// </summary>
        public const string DefaultConfigDirectoryName = ".config";

        /// <summary>
        /// Initializes static members of the <see cref="Config"/> class.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline", Justification = "Keeping static constructor to run Reset logic.")]
        static Config()
        {
            ResolvedSettings = new ConcurrentDictionary<Type, object>();
            ResolvedByNameSettings = new ConcurrentDictionary<string, object>();
            DefaultSources = new Lazy<IEnumerable<ISettingsSource>>(GetDefaultSources);
            certificatePassword = certificateName => null;

            Reset();
        }

        private static string[] DefaultPrecedence()
        {
            var values = new string[0];
            var configuredPrecedence = AppSetting("Its.Configuration.Settings.Precedence");

            if (!string.IsNullOrWhiteSpace(configuredPrecedence))
            {
                values = configuredPrecedence.Split(new[] { "|" }, StringSplitOptions.RemoveEmptyEntries);
            }

            return values.Contains(CommonPrecedence) ? values : values.Concat(new[] { CommonPrecedence }).ToArray();
        }

        /// <summary>
        /// Creates the source.
        /// </summary>
        /// <param name="getSetting">A delegate that gets a setting based on a provided key.</param>
        /// <param name="name">The name (optional) of the source.</param>
        /// <returns>The source.</returns>
        public static ISettingsSource CreateSource(GetSerializedSetting getSetting, string name = null)
        {
            return new AnonymousSettingsSource(getSetting, name);
        }

        /// <summary>
        /// Gets all certificates from locations matching the current precedence within the .config directory.
        /// </summary>
        /// <returns>Certificates from the config directory.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification = "Don't want active work like this in a property.")]
        public static IEnumerable<X509Certificate2> GetCertificatesFromConfigDirectory()
        {
            return GetFiles()
                .Where(f => string.Equals(f.Extension, ".pfx", StringComparison.OrdinalIgnoreCase))
                .Select(f =>
                {
                    var password1 = CertificatePassword(f.Name);
                    if (password1 != null)
                    {
                        return new X509Certificate2(f.FullName, password1);
                    }

                    return new X509Certificate2(f.FullName);
                });
        }

        /// <summary>
        /// Gets certificates from the certificate store.
        /// </summary>
        /// <param name="storeLocation">The store location.</param>
        /// <param name="storeName">The name of the store.</param>
        /// <returns>Certificates from store.</returns>
        public static IEnumerable<X509Certificate2> GetCertificatesFromStore(
            StoreLocation storeLocation = StoreLocation.LocalMachine,
            StoreName storeName = StoreName.My)
        {
            using (var store = new X509Store(storeName, storeLocation))
            {
                store.Open(OpenFlags.ReadOnly);

                var certCollection = store.Certificates;

                return certCollection.OfType<X509Certificate2>();
            }
        }

        /// <summary>
        /// Gets the value for a configuration setting corresponding to the provided key.
        /// </summary>
        /// <returns><see cref="GetSerializedSetting" /> delegate implementation.</returns>
        public static GetSerializedSetting GetSerializedSetting { get; private set; }

        /// <summary>
        /// Gets the serialized setting default.
        /// </summary>
        /// <param name="key">Key to lookup by.</param>
        /// <returns>Serialized settings default.</returns>
        public static string GetSerializedSettingDefault(string key)
        {
            return Sources
                .Select(source => new { source, value = source.GetSerializedSetting(key) })
                .Where(t => !string.IsNullOrWhiteSpace(t.value))
                .Select(t => t.value)
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets the first file in any active .config folder, in order of precedence, that matches the predicate, or null if none match.
        /// </summary>
        /// <param name="matching">A predicate for matching the file.</param>
        /// <returns>First file.</returns>
        public static FileInfo GetFile(Func<FileInfo, bool> matching)
        {
            return GetFiles().FirstOrDefault(matching);
        }

        /// <summary>
        /// Gets the  files in the active config folders that match the specified precedence.
        /// </summary>
        /// <returns>Files in the active config folder.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification = "Don't want active work like this in a property.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Globalization", "CA1308:NormalizeStringsToUppercase", Justification = "Prefer lowercase.")]
        public static IEnumerable<FileInfo> GetFiles()
        {
            return Sources.OfType<ConfigDirectorySettings>()
                .SelectMany(s => s.Files)
                .Reverse()
                .Aggregate(
                    new Dictionary<string, FileInfo>(),
                    (dict, file) =>
                    {
                        dict[file.Name.ToLowerInvariant()] = file;
                        return dict;
                    })
                .Values;
        }

        /// <summary>
        ///     Gets a settings object of the specified type.
        /// </summary>
        /// <param name="type">Type to fetch.</param>
        /// <param name="configurationSerializerRepresentation">Optional; configuration serializer representation; DEFAULT is JSON.</param>
        /// <param name="configurationSerializerFactory">Optional; configuration serializer factory; DEFAULT is JSON.</param>
        /// <returns>Deserialized configuration.</returns>
        public static object Get(Type type, SerializerRepresentation configurationSerializerRepresentation = null, ISerializerFactory configurationSerializerFactory = null)
        {
            return ResolvedSettings.GetOrAdd(type, t =>
            {
                dynamic settingsFor = typeof(For<>).MakeGenericType(type).Construct(configurationSerializerRepresentation ?? serializerRepresentation, configurationSerializerFactory ?? serializerFactory, null);
                return settingsFor.Value;
            });
        }

        /// <summary>
        /// Gets the specified configuration.
        /// </summary>
        /// <typeparam name="T">Type of configuration item.</typeparam>
        /// <param name="configurationSerializerRepresentation">Optional; configuration serializer representation; DEFAULT is JSON.</param>
        /// <param name="configurationSerializerFactory">Optional; configuration serializer factory; DEFAULT is JSON.</param>
        /// <returns>T.</returns>
        public static T Get<T>(SerializerRepresentation configurationSerializerRepresentation = null, ISerializerFactory configurationSerializerFactory = null)
        {
            return (T)ResolvedSettings.GetOrAdd(
                typeof(T),
                t =>
                    new For<T>(
                        configurationSerializerRepresentation ?? serializerRepresentation,
                        configurationSerializerFactory ?? serializerFactory).Value);
        }

        /// <summary>
        ///     Gets a settings object of the specified type.
        /// </summary>
        /// <param name="name">Name of the resource.</param>
        /// <param name="type">Type to fetch.</param>
        /// <param name="configurationSerializerRepresentation">Optional; configuration serializer representation; DEFAULT is JSON.</param>
        /// <param name="configurationSerializerFactory">Optional; configuration serializer factory; DEFAULT is JSON.</param>
        /// <returns>Deserialized configuration.</returns>
        public static object GetByName(
            string name,
            Type type,
            SerializerRepresentation configurationSerializerRepresentation = null,
            ISerializerFactory configurationSerializerFactory = null)
        {
            return ResolvedByNameSettings.GetOrAdd(name, t =>
                                                   {
                                                       dynamic settingsFor = typeof(For<>).MakeGenericType(type).Construct(configurationSerializerRepresentation ?? serializerRepresentation, configurationSerializerFactory ?? serializerFactory, name);
                                                       return settingsFor.Value;
                                                   });
        }

        /// <summary>
        /// Gets the specified configuration.
        /// </summary>
        /// <typeparam name="T">Type of configuration item.</typeparam>
        /// <param name="name">Name of the resource.</param>
        /// <param name="configurationSerializerRepresentation">Optional; configuration serializer representation; DEFAULT is JSON.</param>
        /// <param name="configurationSerializerFactory">Optional; configuration serializer factory; DEFAULT is JSON.</param>
        /// <returns>T.</returns>
        public static T GetByName<T>(
            string name,
            SerializerRepresentation configurationSerializerRepresentation = null,
            ISerializerFactory configurationSerializerFactory = null)
        {
            return (T)ResolvedByNameSettings.GetOrAdd(
                name,
                t =>
                    new For<T>(
                        configurationSerializerRepresentation ?? serializerRepresentation,
                        configurationSerializerFactory        ?? serializerFactory,
                        name).Value);
        }

        /// <summary>
        /// Sets the serialization logic to be used when reading configuration data (from files or otherwise).
        /// </summary>
        /// <param name="configurationSerializerRepresentation">The serializer representation.</param>
        /// <param name="configurationSerializerFactory">Optional serializer factory; DEFAULT is JSON.</param>
        public static void SetSerialization(SerializerRepresentation configurationSerializerRepresentation, ISerializerFactory configurationSerializerFactory = null)
        {
            lock (SyncUpdateSettings)
            {
                Config.serializerRepresentation = configurationSerializerRepresentation;
                Config.serializerFactory = configurationSerializerFactory ?? new JsonSerializerFactory();
            }
        }

        /// <summary>
        ///     Resets settings to the default behavior.
        /// </summary>
        /// <param name="rootDirectoryOverride">Optional root directory to look in override; DEFAULT is (AppDomain.CurrentDomain.BaseDirectory).</param>
        /// <param name="configDirectoryNameOverride">Optional config directory override; DEFAULT is ".config".</param>
        public static void Reset(string rootDirectoryOverride = null, string configDirectoryNameOverride = DefaultConfigDirectoryName)
        {
            lock (SyncUpdateSettings)
            {
                var baseDirectory = rootDirectoryOverride       ?? AppDomain.CurrentDomain.BaseDirectory;
                var directoryName = configDirectoryNameOverride ?? DefaultConfigDirectoryName;

                ResolvedSettings.Clear();
                ResolvedByNameSettings.Clear();
                SettingsDirectory = Path.Combine(baseDirectory, directoryName);
                GetSerializedSetting = GetSerializedSettingDefault;
                sources = DefaultSources.Value;
                precedence = new Lazy<string[]>(DefaultPrecedence);
                serializerRepresentation = new SerializerRepresentation(
                    SerializationKind.Json,
                    typeof(AttemptOnUnregisteredTypeJsonSerializationConfiguration<NullJsonSerializationConfiguration>).ToRepresentation());
                serializerFactory = new JsonSerializerFactory();
            }
        }

        /// <summary>
        /// Gets or sets the sources that are used to look up settings.
        /// </summary>
        /// <remarks>Each source is called in order until one returns a non-null, non-whitespace value, which is the value that is used. Setting this property to null resets it to the default behavior.</remarks>
        public static IEnumerable<ISettingsSource> Sources
        {
            get => sources ?? DefaultSources.Value;
            set => sources = value;
        }

        /// <summary>
        /// Set one to many precedence in order.
        /// </summary>
        /// <param name="precedence">Precedences to use.</param>
        public static void SetPrecedence(params string[] precedence)
        {
            Precedence = precedence;
        }

        /// <summary>
        ///     Gets or sets the precedence of settings folders.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "Keeping array for backwards compatibility for now.")]
        public static string[] Precedence
        {
            get => precedence.Value;
            set => precedence = new Lazy<string[]>(() => value ?? new string[0]);
        }

        /// <summary>
        /// Gets the root directory where file-based settings are looked up.
        /// </summary>
        /// <returns>Settings directory.</returns>
        public static string SettingsDirectory { get; private set; }

        /// <summary>
        /// Gets or sets a function to access the password for a given certificate, given a string representing the certificate's file name.
        /// </summary>
        public static Func<string, SecureString> CertificatePassword
        {
            get => certificatePassword ?? (s => null);
            set => certificatePassword = value;
        }

        /// <summary>
        ///     Gets a setting from AppSettings, checking Azure configuration first and falling back to web.config/app.config if the setting is not found or if it is empty.
        /// </summary>
        /// <param name="key">The key for the setting.</param>
        /// <returns>The configured value.</returns>
        public static string AppSetting(string key)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return ConfigurationManager.AppSettings[key];
        }

        private static IEnumerable<ISettingsSource> GetDefaultSources()
        {
            yield return new EnvironmentVariableSettingsSource();

            foreach (var value in Precedence)
            {
                // e.g. \bin\.config\{value}
                yield return new ConfigDirectorySettings(Path.Combine(SettingsDirectory, value));
            }

            // e.g. \bin\.config
            yield return new ConfigDirectorySettings(SettingsDirectory);

            yield return new AppConfigSettingsSource();
        }

        /// <summary>
        ///     Provides access to settings for a specified type.
        /// </summary>
        /// <typeparam name="T">The type that holds the configuration settings.</typeparam>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "For", Justification = "Remnant of Its.Configuration.")]
        public class For<T>
        {
            private string key;

            /// <summary>
            /// Initializes a new instance of the <see cref="For{T}"/> class.
            /// </summary>
            /// <param name="configurationSerializerRepresentation">The configuration serializer representation.</param>
            /// <param name="configurationSerializerFactory">The configuration serializer factory.</param>
            /// <param name="keyOverride">The optional specified key to use; DEFAULT is null and will build a key from the supplied <typeparamref name="T"/>.</param>
            public For(SerializerRepresentation configurationSerializerRepresentation, ISerializerFactory configurationSerializerFactory, string keyOverride = null)
            {
                this.Key = keyOverride ?? BuildKey();
                var configSetting = GetSerializedSetting(this.Key);

                var serializer = configurationSerializerFactory.BuildSerializer(configurationSerializerRepresentation);

                if (!string.IsNullOrWhiteSpace(configSetting))
                {
                    this.Value = serializer.Deserialize<T>(configSetting);
                }
                else
                {
                    throw new FileNotFoundException(FormattableString.Invariant($"Could not find config for: {typeof(T).AssemblyQualifiedName}."));
                }
            }

            /// <summary>
            ///     Gets the configured settings for type <typeparamref name="T" />.
            /// </summary>
            public T Value { get; private set; }

            /// <summary>
            ///     Gets or sets the key used to look up the settings in configuration.
            /// </summary>
            /// <exception cref="ArgumentException">The key cannot be null, empty, or consist entirely of whitespace.</exception>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1000:DoNotDeclareStaticMembersOnGenericTypes", Justification = "Remnant of Its.Configuration.")]
            public string Key
            {
                get => this.key;
                set
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new ArgumentException("The key cannot be null, empty, or consist entirely of whitespace.");
                    }

                    this.key = value;
                }
            }

            private static string BuildKey()
            {
                var defaultKey = FlattenGenericNames(typeof(T)).ToDelimitedString(string.Empty);

                // abstract members indicate a redirect to a concrete type, specified in AppSettings.
                if (typeof(T).IsAbstract)
                {
                    var redirectedKey = AppSetting(defaultKey);
                    if (!string.IsNullOrWhiteSpace(redirectedKey))
                    {
                        return redirectedKey;
                    }
                }

                return defaultKey;
            }

            private static IEnumerable<string> FlattenGenericNames(Type type)
            {
                if (!type.IsGenericType)
                {
                    yield return type.Name;
                }
                else
                {
                    var genericName = type.GetGenericTypeDefinition().Name;
                    genericName = genericName.Substring(0, genericName.IndexOf("`", StringComparison.InvariantCulture));
                    yield return genericName;

                    yield return "(";

                    bool first = true;

                    foreach (var genericTypeArgument in type.GetGenericArguments())
                    {
                        if (!first)
                        {
                            yield return ",";
                        }

                        yield return FlattenGenericNames(genericTypeArgument).ToDelimitedString(string.Empty);
                        first = false;
                    }

                    yield return ")";
                }
            }
        }

        /// <summary>
        /// Sets the <paramref name="setting"/> object to be returned when <see cref="Get{T}"/> is called for the specified <typeparamref name="TSetting"/>.
        /// </summary>
        /// <typeparam name="TSetting">The type of the setting.</typeparam>
        /// <param name="setting">The setting to return when <see cref="Get{T}"/> is called for the specified <typeparamref name="TSetting"/>.</param>
        public static void Set<TSetting>(TSetting setting)
        {
            if (setting == null)
            {
                throw new ArgumentNullException(nameof(setting));
            }

            ResolvedSettings[typeof(TSetting)] = setting;
        }

        /// <summary>
        /// Sets the <paramref name="setting"/> object to be returned when <see cref="Get{T}"/> is called for the specified <typeparamref name="TSetting"/>.
        /// </summary>
        /// <typeparam name="TSetting">The type of the setting.</typeparam>
        /// <param name="name">Name of the resource.</param>
        /// <param name="setting">The setting to return when <see cref="Get{T}"/> is called for the specified <typeparamref name="TSetting"/>.</param>
        public static void SetByName<TSetting>(string name, TSetting setting)
        {
            if (setting == null)
            {
                throw new ArgumentNullException(nameof(setting));
            }

            ResolvedByNameSettings[name] = setting;
        }

        /// <summary>
        /// Decrypts the contents.
        /// </summary>
        /// <param name="encryptedInput">The encrypted input.</param>
        /// <returns>System.String.</returns>
        public static string DecryptContents(string encryptedInput)
        {
            var certsFromDirectory = Config.GetCertificatesFromConfigDirectory();
            var certsFromStore = CertHelper.GetCertificatesFromStore(StoreLocation.LocalMachine, StoreName.My);
            var allCerts = certsFromDirectory.Concat(certsFromStore).ToList();
            var decryptedValue = encryptedInput.DecryptStringFromBase64String(allCerts);
            return decryptedValue;
        }
    }

    /// <summary>
    ///     Provides access to settings.
    /// </summary>
    public interface ISettingsSource
    {
        /// <summary>
        ///     Gets a settings string corresponding to the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>A string representing serialized settings.</returns>
        string GetSerializedSetting(string key);

        /// <summary>
        ///     Gets the name of the settings source.
        /// </summary>
        string Name { get; }
    }

    /// <summary>
    /// Provides settings based on a set of files in a specified configuration folder.
    /// </summary>
    public class ConfigDirectorySettings : ISettingsSource
    {
        private readonly string directoryPath;
        private readonly Dictionary<string, string> fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> secureSettings = new HashSet<string>();
        private readonly List<FileInfo> files = new List<FileInfo>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigDirectorySettings"/> class.
        /// </summary>
        /// <param name="directoryPath">The directory path to where the configuration files are located.</param>
        public ConfigDirectorySettings(string directoryPath)
        {
            this.directoryPath = directoryPath;
            this.ReadFiles();
        }

        private void ReadFiles()
        {
            var directory = new DirectoryInfo(this.directoryPath);

            if (directory.Exists)
            {
                foreach (var file in directory.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                                              .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    this.files.Add(file);
                    if (string.Equals(file.Extension, ".json", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var reader = new StreamReader(file.FullName))
                        {
                            var content = reader.ReadToEnd();
                            var key = GetKey(file);

                            this.fileContents.Add(key, content);
                        }
                    }
                    else if (string.Equals(file.Extension, ".secure", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var reader = new StreamReader(file.FullName))
                        {
                            var content = reader.ReadToEnd();
                            var key = GetKey(file);

                            key = key.Remove(key.Length - ".json".Length);
                            this.secureSettings.Add(key);

                            try
                            {
                                this.fileContents.Add(key, content);
                            }
                            catch (ArgumentException)
                            {
                                throw new ConfigurationErrorsException(FormattableString.Invariant($"Found conflicting settings file: {file.FullName}"));
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        ///     Gets a settings string corresponding to the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>A string representing serialized settings.</returns>
        public string GetSerializedSetting(string key)
        {
            if (this.fileContents.TryGetValue(key, out var value))
            {
                if (this.secureSettings.Contains(key))
                {
                    var decryptedValue = Config.DecryptContents(value);
                    return decryptedValue;
                }

                return value;
            }

            return null;
        }

        /// <summary>
        /// Gets the list of files found in the configuration directory.
        /// </summary>
        public IReadOnlyCollection<FileInfo> Files => this.files;

        /// <summary>
        ///     Gets the name of the settings source.
        /// </summary>
        public string Name => "settings folder (" + this.directoryPath + ")";

        private static string GetKey(FileInfo file)
        {
            return file.Name.Remove(file.Name.Length - file.Extension.Length);
        }
    }

    /// <summary>
    /// Accesses a settings string corresponding to the specified key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>A string representing serialized settings.</returns>
    public delegate string GetSerializedSetting(string key);

    /// <summary>
    /// Anonymous implementation of <see cref="ISettingsSource" />.
    /// </summary>
    internal class AnonymousSettingsSource : ISettingsSource
    {
        private readonly GetSerializedSetting getSetting;
        private readonly string name;

        /// <summary>
        /// Initializes a new instance of the <see cref="AnonymousSettingsSource"/> class.
        /// </summary>
        /// <param name="getSetting">Setting getter.</param>
        /// <param name="name">Optional name of the setting.</param>
        public AnonymousSettingsSource(GetSerializedSetting getSetting, string name = null)
        {
            this.getSetting = getSetting ?? throw new ArgumentNullException(nameof(getSetting));
            this.name = name;
        }

        /// <inheritdoc />
        public string GetSerializedSetting(string key)
        {
            return this.getSetting(key);
        }

        /// <inheritdoc />
        public string Name => this.name ?? this.getSetting.ToString();
    }

    /// <summary>
    /// AppSettings implementation of <see cref="ISettingsSource" />.
    /// </summary>
    internal class AppConfigSettingsSource : ISettingsSource
    {
        /// <inheritdoc />
        public string GetSerializedSetting(string key)
        {
            return ConfigurationManager.AppSettings[key];
        }

        /// <inheritdoc />
        public string Name => "web.config / app.config";
    }

    /// <summary>
    /// Environment variable implementation of <see cref="ISettingsSource" />.
    /// </summary>
    internal class EnvironmentVariableSettingsSource : ISettingsSource
    {
        /// <inheritdoc />
        public string GetSerializedSetting(string key)
        {
            return Environment.GetEnvironmentVariable(key);
        }

        /// <inheritdoc />
        public string Name => "environment variable";
    }
}
