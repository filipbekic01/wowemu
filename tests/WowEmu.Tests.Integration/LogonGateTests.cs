using System.Net.Sockets;

namespace WowEmu.Tests.Integration;

/// <summary>
/// The M1 gate as a permanent regression test — PLAN.md §9.6.
/// </summary>
/// <remarks>
/// <c>tools/harness/m1_login.py</c> proves the same things against a server you started by hand.
/// This proves them on every push, which is the difference between knowing M1 worked once and
/// knowing it still works.
/// </remarks>
[Collection(AuthServerSuite.Name)]
public sealed class LogonGateTests(AuthServerFixture server)
{
    [MySqlFact]
    public async Task Logon_WithCorrectPassword_DerivesTheSameSessionKeyAsTheServer()
    {
        using Socket socket = await server.ConnectAsync();

        LogonResult result = await LogonConversation.LogonAsync(
            socket, AuthServerFixture.Username, AuthServerFixture.Password);

        // Verified inside the conversation via M2; asserted here so the test states its own claim.
        Assert.Equal(40, result.SessionKey.Length);
        Assert.NotEqual(new byte[40], result.SessionKey);
    }

    [MySqlFact]
    public async Task RealmList_AfterLogon_ContainsTheSeededRealm()
    {
        using Socket socket = await server.ConnectAsync();

        LogonResult result = await LogonConversation.LogonAsync(
            socket, AuthServerFixture.Username, AuthServerFixture.Password);

        // Seeded by the InitialAuthSchema migration. If the realm list is empty the client reaches
        // character select and finds nothing to connect to, which is M1 failing in the only way a
        // unit test cannot see.
        Assert.NotEmpty(result.Realms);
        Assert.Contains(result.Realms, realm => realm.Name == "WowEmu");
    }

    [MySqlFact]
    public async Task Reconnect_OnAFreshConnection_ProvesTheSessionKeyWasPersisted()
    {
        byte[] sessionKey;

        using (Socket first = await server.ConnectAsync())
        {
            LogonResult result = await LogonConversation.LogonAsync(
                first, AuthServerFixture.Username, AuthServerFixture.Password);

            sessionKey = result.SessionKey;
        }

        // A second socket, so nothing in the server's per-connection state can carry the key over.
        // It can only come back out of the database.
        using Socket second = await server.ConnectAsync();

        await LogonConversation.ReconnectAsync(second, AuthServerFixture.Username, sessionKey);
    }

    [MySqlFact]
    public async Task Logon_WithTheWrongPassword_IsRefused()
    {
        using Socket socket = await server.ConnectAsync();

        AuthRefusedException refusal = await Assert.ThrowsAsync<AuthRefusedException>(
            () => LogonConversation.LogonAsync(socket, AuthServerFixture.Username, "NOTTHEPASSWORD"));

        Assert.Equal(0x04, refusal.Code);
    }

    /// <summary>
    /// A wrong password and an account that does not exist must be indistinguishable.
    /// </summary>
    /// <remarks>
    /// Upstream never returns <c>FailIncorrectPassword</c> for exactly this reason: any difference
    /// between the two answers turns the logon server into an oracle for which account names are
    /// real. It is the kind of property that gets "fixed" by someone making error messages more
    /// helpful, so it is pinned here.
    /// </remarks>
    [MySqlFact]
    public async Task Logon_WithAnUnknownAccount_IsRefusedWithTheSameCodeAsAWrongPassword()
    {
        using Socket wrongPassword = await server.ConnectAsync();
        using Socket unknownAccount = await server.ConnectAsync();

        AuthRefusedException forWrongPassword = await Assert.ThrowsAsync<AuthRefusedException>(
            () => LogonConversation.LogonAsync(wrongPassword, AuthServerFixture.Username, "NOTTHEPASSWORD"));

        AuthRefusedException forUnknownAccount = await Assert.ThrowsAsync<AuthRefusedException>(
            () => LogonConversation.LogonAsync(unknownAccount, "NOSUCHACCOUNT", AuthServerFixture.Password));

        Assert.Equal(forWrongPassword.Code, forUnknownAccount.Code);
    }
}
