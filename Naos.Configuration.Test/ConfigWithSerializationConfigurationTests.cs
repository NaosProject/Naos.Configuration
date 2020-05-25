// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ConfigWithSerializationConfigurationTests.cs" company="Naos Project">
//    Copyright (c) Naos Project 2019. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace Naos.Configuration.Test
{
    using System;
    using System.Collections.Generic;
    using FluentAssertions;
    using Naos.Configuration.Domain;
    using OBeautifulCode.Serialization.Json;
    using Xunit;

    public static class ConfigWithSerializationConfigurationTests
    {
        [Fact]
        public static void File_exists__Gets_deserialized()
        {
            // Arrange
            var expected = new Contains
            {
                Property = new DerivedProperty
                {
                    PropertyOnBase = "propBase",
                    PropertyOnDerived = "propDerived",
                },
            };

            var serializer = new ObcJsonSerializer(typeof(PropertyJsonConfig).ToJsonSerializationConfigurationType());
            var expectedJson = serializer.SerializeToString(expected);
            var jsonConfigurationType = typeof(PropertyJsonConfig);

            // Act
            Config.Reset();
            var actual = Config.Get(expected.GetType(), jsonConfigurationType.ToJsonSerializationConfigurationType());
            var actualJson = serializer.SerializeToString(actual);

            Config.Reset();
            var actualGeneric = Config.Get<Contains, PropertyJsonConfig>();
            var actualGenericJson = serializer.SerializeToString(actualGeneric);

            var exceptionGet = Record.Exception(() =>
            {
                Config.Reset();
                return Config.Get(expected.GetType());
            });
            var exceptionGetGeneric = Record.Exception(() =>
            {
                Config.Reset();
                return Config.Get<Contains>();
            });

            // Assert
            exceptionGet.Should().NotBeNull();
            exceptionGet.InnerException.Should().NotBeNull();
            exceptionGet.InnerException.Message.Should().StartWith("Could not create an instance of type Naos.Configuration.Test.IBase. Type is an interface or abstract class and cannot be instantiated.");
            exceptionGetGeneric.Should().NotBeNull();
            exceptionGetGeneric.Message.Should().StartWith("Could not create an instance of type Naos.Configuration.Test.IBase. Type is an interface or abstract class and cannot be instantiated.");
            actualJson.Should().Be(expectedJson);
            actualGenericJson.Should().Be(expectedJson);
        }
    }

    public interface IBase
    {
        string PropertyOnBase { get; set; }
    }

    public abstract class BaseProperty : IBase
    {
        public string PropertyOnBase { get; set; }
    }

    public class DerivedProperty : BaseProperty
    {
        public string PropertyOnDerived { get; set; }
    }

    public class Contains
    {
        public IBase Property { get; set; }
    }

    public class PropertyJsonConfig : JsonSerializationConfigurationBase
    {
        protected override IReadOnlyCollection<TypeToRegisterForJson> TypesToRegisterForJson =>
            new[] { typeof(IBase).ToTypeToRegisterForJson(), typeof(Contains).ToTypeToRegisterForJson(), typeof(BaseProperty).ToTypeToRegisterForJson(), };
    }
}
