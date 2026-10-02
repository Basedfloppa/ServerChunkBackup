using System.Text;
using Vintagestory.API.Common.Entities;

namespace VsFullCapture;

/// <summary>
/// The savegame form of an entity — exactly what a chunk row keeps for it:
/// the class name followed by <c>Entity.ToBytes(writer, forClient: false)</c>
/// (see <c>ServerChunk.FastSerializeEntities</c>).
///
/// Why this has to happen inside the mod: over the network an entity travels in
/// "sync" form (<c>Entity.FromBytes(reader, isSync: true)</c>), and the savegame form
/// cannot be assembled from it — it lacks the version string and the attributes tree.
/// The mod holds the live entity object, so it can ask it for the savegame bytes with
/// the same call the game makes when saving a chunk.
/// </summary>
public static class EntitySaveData
{
    /// <summary>
    /// Class name to write. Taken from the entity type, because that is the name the
    /// entity was instantiated with (<c>ClassRegistry.CreateEntity(entityType)</c>
    /// resolves <c>entityType.Class</c>): it is guaranteed to resolve on load as well.
    ///
    /// <c>RegistryObject.Class</c> is filled by <c>Entity.Initialize</c> from the same
    /// place, the fallback covers an entity that was somehow not initialized.
    /// </summary>
    public static string? ClassnameOf(Entity entity)
        => string.IsNullOrEmpty(entity.Class) ? entity.Properties?.Class : entity.Class;

    /// <summary>
    /// Savegame bytes of the entity, without the class name.
    ///
    /// Must be called on the main thread: the game's serializer reads the live state and
    /// writes the head rotation into WatchedAttributes.
    /// </summary>
    public static byte[] Extract(Entity entity)
    {
        using var ms = new MemoryStream(4096);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        entity.ToBytes(w, forClient: false);
        w.Flush();
        return ms.ToArray();
    }
}
