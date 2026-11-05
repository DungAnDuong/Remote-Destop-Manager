extern alias rdmclient;

using System.IO;
using RDM.Shared.Common;
using Xunit;
using ClientState = rdmclient::RDM.Client.ClientState;
using RelayDefaults = rdmclient::RDM.Client.RelayDefaults;

namespace RDM.Tests;

public class RelayDefaultsTests
{
    private const string Text = "host=relay.example\nport=31408\npin=abc\nenrollment=code\n";

    [Fact]
    public void Parse_ReadsAllFields()
    {
        var d = RelayDefaults.Parse(Text.Replace("\n", "\r\n"));
        Assert.Equal("relay.example", d.Host);
        Assert.Equal(31408, d.Port);
        Assert.Equal("abc", d.Pin);
        Assert.Equal("code", d.EnrollmentToken);
    }

    [Theory]
    [InlineData("port=31408\n")]
    [InlineData("host=a\nport=70000\n")]
    [InlineData("host=a\nport=x\n")]
    [InlineData("host=a\nunknown=1\n")]
    [InlineData("host\n")]
    public void Parse_RejectsBadInput(string text) => Assert.Throws<InvalidDataException>(() => RelayDefaults.Parse(text));

    [Fact]
    public void Apply_FillsEmptyState_ThenReportsNoChange()
    {
        var s = new ClientState();
        Assert.True(RelayDefaults.Parse(Text).ApplyTo(s));
        Assert.Equal("relay.example", s.RelayHost);
        Assert.Equal(31408, s.RelayPort);
        Assert.Equal("abc", s.RelayPin);
        Assert.Equal("code", s.EnrollmentToken);
        Assert.False(RelayDefaults.Parse(Text).ApplyTo(s));
    }

    /// <summary>Bản phát hành không có ô sửa: giá trị cũ hay khác phải được thay, kể cả khi đã đọc lại từ file.</summary>
    [Fact]
    public void Apply_ReplacesOtherValues_AndSurvivesReload()
    {
        using var t = new TempStore();
        var s = t.Store.LoadOrCreate();
        s.RelayHost = "old.example"; s.RelayPort = 9000; s.RelayPin = "old"; s.EnrollmentToken = "oldToken";
        Assert.True(RelayDefaults.Parse(Text).ApplyTo(s));
        t.Store.Save(s);
        var reloaded = t.Store.LoadOrCreate();
        Assert.Equal("relay.example", reloaded.RelayHost);
        Assert.Equal(31408, reloaded.RelayPort);
        Assert.Equal("abc", reloaded.RelayPin);
        Assert.Equal("code", reloaded.EnrollmentToken);
    }

    [Fact]
    public void RouteName_HidesRelayOnlyInManagedMode()
    {
        var before = ProductMode.Managed;
        try
        {
            ProductMode.Managed = false;
            Assert.Equal("Relay", ProductMode.RelayRouteName);
            ProductMode.Managed = true;
            Assert.Equal("Internet", ProductMode.RelayRouteName);
        }
        finally { ProductMode.Managed = before; }
    }

    /// <summary>Mã nguồn không được chứa máy chủ thật: bản Debug dùng cho kiểm thử không mang cấu hình đóng gói.</summary>
    [Fact]
    public void DebugBuild_HasNoEmbeddedDefaults()
    {
#if DEBUG
        Assert.Null(RelayDefaults.LoadEmbedded());
#else
        Assert.True(true);
#endif
    }
}
