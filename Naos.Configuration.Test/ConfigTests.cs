// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ConfigTests.cs" company="Naos">
//    Copyright (c) Naos 2017. All Rights Reserved.
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
        public static void File_does_not_exist__Gets_default()
        {
            var config = Config.Get<TestConfigObjectNotThere>();
            config.Should().Be(default(TestConfigObjectNotThere));
            config.Should().BeNull();
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
