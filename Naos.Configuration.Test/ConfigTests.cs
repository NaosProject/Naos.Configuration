// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ConfigTests.cs" company="Naos Project">
//    Copyright (c) Naos Project 2019. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace Naos.Configuration.Test
{
    using FluentAssertions;
    using Naos.Configuration.Domain;
    using Xunit;

    public static class ConfigTests
    {
        [Fact]
        public static void File_exists__Gets_deserialized()
        {
            Config.Precedence = new[] { "Common" };
            var config = Config.Get<TestConfigObject>();
            config.Should().NotBeNull();
            config.Property.Should().Be("Something");
        }

        [Fact]
        public static void File_does_not_exist__Throws()
        {
            var exception = Record.Exception(() => Config.Get<TestConfigObjectNotThere>());
            exception.Should().NotBeNull();
            exception.Message.Should().Be("Could not find config for: Naos.Configuration.Test.TestConfigObjectNotThere.");
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
