// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ConfigTests.cs" company="Naos Project">
//    Copyright (c) Naos Project 2019. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace Naos.Configuration.Test
{
    using FluentAssertions;
    using Naos.Configuration.Domain;
    using OBeautifulCode.Reflection.Recipes;
    using Xunit;

    public static class ConfigTests
    {
        [Fact]
        public static void Get___File_exists__Gets_deserialized()
        {
            Config.SetPrecedence("Uncommon");
            var config = Config.Get<TestConfigObject>();
            config.Should().NotBeNull();
            config.Property.Should().Be("Something");
        }

        [Fact]
        public static void GetByName___File_exists__Gets_deserialized()
        {
            Config.SetPrecedence("Named");
            var config = Config.GetByName("Item", typeof(TestConfigObject));
            config.Should().NotBeNull();
            config.GetPropertyValue(nameof(TestConfigObject.Property)).Should().Be("Something");
        }

        [Fact]
        public static void GetByName_T___File_exists__Gets_deserialized()
        {
            Config.SetPrecedence("Named");
            var config = Config.GetByName<TestConfigObject>("Item");
            config.Should().NotBeNull();
            config.Property.Should().Be("Something");
        }

        [Fact]
        public static void File_does_not_exist__Throws()
        {
            var exception = Record.Exception(() => Config.Get<TestConfigObjectNotThere>());
            exception.Should().NotBeNull();
            exception.Message.Should()
                     .StartWith("Could not find config for: Naos.Configuration.Test.TestConfigObjectNotThere, Naos.Configuration.Test,");
        }
    }

    public class TestConfigObject
    {
        public TestConfigObject(string property)
        {
            this.Property = property;
        }

        public string Property { get; private set; }
    }

    public class TestConfigObjectNotThere
    {
    }
}
