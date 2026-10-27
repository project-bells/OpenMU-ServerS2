// <copyright file="GuildCreateAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Guild;

using System.ComponentModel;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.Guild;

/// <summary>
/// Action to create a guild.
/// </summary>
public class GuildCreateAction
{
    /// <summary>
    /// Creates the guild.
    /// </summary>
    /// <param name="creator">The creator.</param>
    /// <param name="guildName">Name of the guild.</param>
    /// <param name="guildEmblem">The guild emblem.</param>
    public async ValueTask CreateGuildAsync(Player creator, string guildName, byte[] guildEmblem)
    {
        using var loggerScope = creator.Logger.BeginScope(this.GetType());
        if (creator.PlayerState.CurrentState != PlayerState.EnteredWorld)
        {
            creator.Logger.LogError($"Account {creator.Account?.LoginName} not in the right state, but {creator.PlayerState.CurrentState}.");
            return;
        }

        // All supported MU clients reserve exactly eight one-byte characters.
        // Keep Thai lossless through CP874 and reject values which would become
        // '?' on the wire (and could otherwise collide in persistence).
        if (guildName.Length is < 4 or > 8 || guildName.Any(character => !IsAllowedGuildNameCharacter(character)))
        {
            await creator.InvokeViewPlugInAsync<IShowGuildCreateResultPlugIn>(
                p => p.ShowGuildCreateResultAsync(GuildCreateErrorDetail.GuildAlreadyExist)).ConfigureAwait(false);
            return;
        }

        if (creator.GameContext.PlugInManager.GetPlugInPoint<IGuildCreatingPlugIn>() is { } plugInPoint)
        {
            var eventArgs = new CancelEventArgs();
            await plugInPoint.GuildCreatingAsync(creator, eventArgs).ConfigureAwait(false);
            if (eventArgs.Cancel)
            {
                return;
            }
        }

        var guildServer = (creator.GameContext as IGameServerContext)?.GuildServer;
        if (guildServer is null)
        {
            creator.Logger.LogError($"No guild server available");
            return;
        }

        if (await guildServer.GuildExistsAsync(guildName).ConfigureAwait(false))
        {
            await creator.InvokeViewPlugInAsync<IShowGuildCreateResultPlugIn>(p => p.ShowGuildCreateResultAsync(GuildCreateErrorDetail.GuildAlreadyExist)).ConfigureAwait(false);
            return;
        }

        if (await guildServer.CreateGuildAsync(guildName, creator.SelectedCharacter!.Name, creator.SelectedCharacter.Id, guildEmblem, ((IGameServerContext)creator.GameContext).Id).ConfigureAwait(false))
        {
            await creator.InvokeViewPlugInAsync<IShowGuildCreateResultPlugIn>(p => p.ShowGuildCreateResultAsync(GuildCreateErrorDetail.None)).ConfigureAwait(false);
            creator.Logger.LogInformation("Guild created: [{0}], Master: [{1}]", guildName, creator.SelectedCharacter.Name);
        }
        else
        {
            await creator.InvokeViewPlugInAsync<IShowGuildCreateResultPlugIn>(p => p.ShowGuildCreateResultAsync(GuildCreateErrorDetail.GuildAlreadyExist)).ConfigureAwait(false);
        }
    }

    private static bool IsAllowedGuildNameCharacter(char character) => character is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or >= '\u0E01' and <= '\u0E3A'
        or '\u0E3F'
        or >= '\u0E40' and <= '\u0E5B';
}
