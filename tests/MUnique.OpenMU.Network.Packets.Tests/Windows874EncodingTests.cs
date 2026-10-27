// <copyright file="Windows874EncodingTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Network.Packets.Tests;

using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.Packets.ServerToClient;

/// <summary>
/// Tests the Thai wire encoding used by classic ten-byte identity fields.
/// </summary>
[TestFixture]
public class Windows874EncodingTests
{
    /// <summary>
    /// Ensures that supported Thai character names use one byte per code point and round-trip.
    /// </summary>
    /// <param name="name">The character name.</param>
    [TestCase("สุดเขต")]
    [TestCase("สมยศ")]
    [TestCase("เพลินจิต")]
    public void ThaiNameRoundTrips(string name)
    {
        byte[] bytes = Windows874Encoding.Instance.GetBytes(name);

        Assert.That(bytes, Has.Length.EqualTo(name.Length));
        Assert.That(Windows874Encoding.Instance.GetString(bytes), Is.EqualTo(name));
    }

    /// <summary>
    /// Verifies that unsupported input is detected before it reaches a fixed field.
    /// </summary>
    [TestCase("อัศวิน", true)]
    [TestCase("Aegis123", true)]
    [TestCase("อัศวิน😀", false)]
    [TestCase("กิลด์é", false)]
    public void RepresentabilityIsDetected(string value, bool expected)
    {
        Assert.That(Windows874Encoding.IsRepresentable(value), Is.EqualTo(expected));
    }

    /// <summary>
    /// Verifies the real eight-byte guild creation field, not just the codec in isolation.
    /// </summary>
    [Test]
    public void ThaiGuildNameRoundTripsThroughPacket()
    {
        var packet = new GuildCreateRequest(new byte[GuildCreateRequest.Length]);
        packet.GuildName = "อัศวิน";

        Assert.That(packet.GuildName, Is.EqualTo("อัศวิน"));
    }

    /// <summary>
    /// Verifies that variable-length announcements remain UTF-8.
    /// </summary>
    [Test]
    public void ThaiAnnouncementRoundTripsThroughPacket()
    {
        const string message = "ประกาศ: เซิร์ฟเวอร์ Aegis-Mu พร้อมให้บริการ";
        var packet = new ServerMessage(new byte[ServerMessage.GetRequiredSize(message)]);
        packet.Message = message;

        Assert.That(packet.Message, Is.EqualTo(message));
    }
}
