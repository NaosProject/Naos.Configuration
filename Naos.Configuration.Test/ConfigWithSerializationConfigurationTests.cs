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
    using OBeautifulCode.Representation.System;
    using OBeautifulCode.Serialization;
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

            var configurationSerializerRepresentation = new SerializerRepresentation(SerializationKind.Json, typeof(PropertyJsonConfig).ToRepresentation());
            var serializer = new ObcJsonSerializer(typeof(PropertyJsonConfig).ToJsonSerializationConfigurationType());
            var expectedJson = serializer.SerializeToString(expected);

            // Act - Method specified serialization.
            Config.Reset();
            var actualViaParameterMethodSpecific = Config.Get(expected.GetType(), configurationSerializerRepresentation);
            var actualJsonViaParameterMethodSpecific = serializer.SerializeToString(actualViaParameterMethodSpecific);
            var actualViaGenericMethodSpecific = Config.Get(expected.GetType(), configurationSerializerRepresentation);
            var actualJsonViaGenericMethodSpecific = serializer.SerializeToString(actualViaGenericMethodSpecific);

            // Act - Default via type parameter.
            var exceptionGet = Record.Exception(() =>
            {
                Config.Reset();
                return Config.Get(expected.GetType());
            });

            // Act - Default via generic call.
            var exceptionGetGeneric = Record.Exception(() =>
            {
                Config.Reset();
                return Config.Get<Contains>();
            });

            // Act - Global specified
            Config.Reset();
            Config.SetSerialization(configurationSerializerRepresentation);
            var actualViaParameterGlobal = Config.Get(expected.GetType());
            var actualJsonViaParameterGlobal = serializer.SerializeToString(actualViaParameterGlobal);
            var actualViaGenericGlobal = Config.Get<Contains>();
            var actualJsonViaGenericGlobal = serializer.SerializeToString(actualViaGenericGlobal);

            // Assert
            exceptionGet.Should().NotBeNull();
            exceptionGet.InnerException.Should().NotBeNull();
            exceptionGet.InnerException.Message.Should().StartWith("Could not create an instance of type Naos.Configuration.Test.IBase. Type is an interface or abstract class and cannot be instantiated.");
            exceptionGetGeneric.Should().NotBeNull();
            exceptionGetGeneric.Message.Should().StartWith("Could not create an instance of type Naos.Configuration.Test.IBase. Type is an interface or abstract class and cannot be instantiated.");
            actualJsonViaParameterMethodSpecific.Should().Be(expectedJson);
            actualJsonViaGenericMethodSpecific.Should().Be(expectedJson);
            actualJsonViaParameterGlobal.Should().Be(expectedJson);
            actualJsonViaGenericGlobal.Should().Be(expectedJson);
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
