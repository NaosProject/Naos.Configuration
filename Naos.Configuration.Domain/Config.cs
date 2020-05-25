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
    using System.Security;
    using System.Security.Cryptography.Pkcs;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using OBeautifulCode.Collection.Recipes;
    using OBeautifulCode.Reflection.Recipes;
    using OBeautifulCode.Security.Recipes;
    using OBeautifulCode.Serialization;
    using OBeautifulCode.Serialization.Json;

    /// <summary>
    /// Config retrieval entry harness.
    /// </summary>
    public static class Config
    {
        private static readonly ConcurrentDictionary<Type, object> ResolvedSettings;
        private static readonly Lazy<IEnumerable<ISettingsSource>> DefaultSources;
        private static readonly ObcJsonSerializer DefaultJsonSerializer = new ObcJsonSerializer();
        private static Func<string, SecureString> certificatePassword;
        private static IEnumerable<ISettingsSource> sources;
        private static Lazy<string[]> precedence;

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
        /// Gets or sets configuration settings for deserializing.
        /// </summary>
        public static DeserializeSettings Deserialize { get; set; }

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
        /// Implements the default settings deserialization method, which is to deserialize the specified string using <see cref="ObcJsonSerializer" />.
        /// </summary>
        /// <param name="targetType">Target type.</param>
        /// <param name="serialized">Serialized text.</param>
        /// <returns>Deserialized default.</returns>
        public static object DeserializeDefault(Type targetType, string serialized)
        {
            return DefaultJsonSerializer.Deserialize(serialized, targetType);
        }

        /// <summary>
        ///     Gets a settings object of the specified type.
        /// </summary>
        /// <param name="type">Type to fetch.</param>
        /// <param name="jsonConfigurationType">Optional <see cref="JsonSerializationConfigurationBase" /> implementation for specific serialization.</param>
        /// <returns>Deserialized configuration.</returns>
        public static object Get(Type type, JsonSerializationConfigurationType jsonConfigurationType = null)
        {
            return ResolvedSettings.GetOrAdd(type, t =>
            {
                dynamic settingsFor = typeof(For<>).MakeGenericType(type).Construct(jsonConfigurationType);
                return settingsFor.Value;
            });
        }

        /// <summary>
        /// Gets a settings object of the specified type.
        /// </summary>
        /// <param name="jsonConfigurationType">Optional <see cref="JsonSerializationConfigurationBase" /> implementation for specific serialization.</param>
        /// <typeparam name="T">Type of configuration.</typeparam>
        /// <returns>Deserialized configuration.</returns>
        public static T Get<T>(JsonSerializationConfigurationType jsonConfigurationType = null)
        {
            return (T)ResolvedSettings.GetOrAdd(typeof(T), t => new For<T>(jsonConfigurationType).Value);
        }

        /// <summary>
        /// Gets a settings object of the specified type.
        /// </summary>
        /// <typeparam name="TConfig">Type of configuration.</typeparam>
        /// <typeparam name="TJsonConfiguration">Type of JSON configuration.</typeparam>
        /// <returns>Deserialized configuration.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter", Justification = "Keeping this option.")]
        public static TConfig Get<TConfig, TJsonConfiguration>()
        {
            return (TConfig)ResolvedSettings.GetOrAdd(typeof(TConfig), t => new For<TConfig>(typeof(TJsonConfiguration).ToJsonSerializationConfigurationType()).Value);
        }

        /// <summary>
        ///     Resets settings to the default behavior.
        /// </summary>
        /// <param name="rootDirectoryOverride">Optional root directory to look in override; DEFAULT is (AppDomain.CurrentDomain.BaseDirectory).</param>
        /// <param name="configDirectoryNameOverride">Optional config directory override; DEFAULT is ".config".</param>
        public static void Reset(string rootDirectoryOverride = null, string configDirectoryNameOverride = DefaultConfigDirectoryName)
        {
            var baseDirectory = rootDirectoryOverride ?? AppDomain.CurrentDomain.BaseDirectory;
            var directoryName = configDirectoryNameOverride ?? DefaultConfigDirectoryName;

            ResolvedSettings.Clear();
            SettingsDirectory = Path.Combine(baseDirectory, directoryName);
            Deserialize = DeserializeDefault;
            GetSerializedSetting = GetSerializedSettingDefault;
            sources = DefaultSources.Value;
            precedence = new Lazy<string[]>(DefaultPrecedence);
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
            private static string key = BuildKey();

            /// <summary>
            ///     Initializes a new instance of the <see cref="For{T}" /> class.
            /// </summary>
            /// <param name="jsonConfigurationType">Optional <see cref="JsonSerializationConfigurationBase" /> implementation for specific serialization.</param>
            public For(JsonSerializationConfigurationType jsonConfigurationType = null)
            {
                var configSetting = GetSerializedSetting(Key);

                var targetType = typeof(T);

                DeserializeSettings deserializer = Deserialize;
                if (jsonConfigurationType != null)
                {
                    var attemptingJsonConfigurationType = typeof(AttemptOnUnregisteredTypeJsonSerializationConfiguration<>).MakeGenericType(jsonConfigurationType.ConcreteSerializationConfigurationDerivativeType);
                    deserializer = (type, serializedString) => new ObcJsonSerializer(attemptingJsonConfigurationType.ToJsonSerializationConfigurationType()).Deserialize(serializedString, type);
                }

                if (!string.IsNullOrWhiteSpace(configSetting))
                {
                    this.Value = (T)deserializer(targetType, configSetting);
                }
                else
                {
                    throw new FileNotFoundException("Could not find config for: " + targetType.FullName + ".");
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
            public static string Key
            {
                get => key;
                set
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new ArgumentException("The key cannot be null, empty, or consist entirely of whitespace.");
                    }

                    key = value;
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
                throw new ArgumentNullException("setting");
            }

            ResolvedSettings[typeof(TSetting)] = setting;
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
                    var certsFromDirectory = Config.GetCertificatesFromConfigDirectory();
                    var certsFromStore = CertHelper.GetCertificatesFromStore(StoreLocation.LocalMachine, StoreName.My);
                    var allCerts = certsFromDirectory.Concat(certsFromStore).ToList();
                    var decryptedValue = value.DecryptStringFromBase64String(allCerts);
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
    /// Deserializes a string into a specified type.
    /// </summary>
    /// <param name="targetType">The type to which the settings should be deserialized.</param>
    /// <param name="serialized">The serialized settings.</param>
    /// <returns>An instance of <paramref name="targetType" />.</returns>
    public delegate object DeserializeSettings(Type targetType, string serialized);

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
