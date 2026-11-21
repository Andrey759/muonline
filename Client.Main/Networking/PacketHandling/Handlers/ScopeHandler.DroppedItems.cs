using Client.Main.Core.Models;
using System.Collections.Concurrent;

namespace Client.Main.Networking.PacketHandling.Handlers
{
    public partial class ScopeHandler
    {
        private static readonly ConcurrentQueue<DroppedItemWorkItem> _droppedItemQueue = new();
        private static int _droppedItemWorkerRunning;

        // Every drop add and every removal of an id bumps its version. A drop still loading (its model is decoded
        // in the background, one item at a time) is published only if its version is still current: otherwise a
        // removal (picked up, expired, out of view) that arrived during loading was lost and the item stayed on the
        // ground as a ghost that cannot be picked up.
        private static readonly ConcurrentDictionary<ushort, int> _dropVersions = new();

        private static int BumpDropVersion(ushort maskedId) =>
            _dropVersions.AddOrUpdate(maskedId, 1, static (_, v) => unchecked(v + 1));

        private static bool IsDropVersionCurrent(ushort maskedId, int version) =>
            _dropVersions.TryGetValue(maskedId, out int current) && current == version;

        private readonly struct DroppedItemWorkItem
        {
            public DroppedItemWorkItem(
                ScopeObject dropObj,
                ushort maskedId,
                string soundPath,
                bool isFreshDrop,
                int version)
            {
                DropObject = dropObj;
                MaskedId = maskedId;
                SoundPath = soundPath;
                IsFreshDrop = isFreshDrop;
                Version = version;
            }

            public ScopeObject DropObject { get; }
            public ushort MaskedId { get; }
            public string SoundPath { get; }
            public bool IsFreshDrop { get; }
            public int Version { get; }
        }
    }
}
