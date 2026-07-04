// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared.VendingMachines;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 6 core: the reflective state capture/restore that owns fidelity the engine
    /// serializer drops. Proves round-trip on the exact type that crashed the roster sweep
    /// on 72/161 ships — a populated <see cref="Dictionary{TKey,TValue}"/> of
    /// <see cref="VendingMachineInventoryEntry"/> (which the engine YAML serializer cannot
    /// write) — WITHOUT any content modification.
    /// </summary>
    [TestFixture]
    public sealed class ReflectiveStateCaptureTest
    {
        [Test]
        public async Task VendingInventoryDict_RoundTrips()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var serialization = server.ResolveDependency<ISerializationManager>();

            var capture = new ReflectiveStateCapture(serialization);

            var original = new Dictionary<string, VendingMachineInventoryEntry>
            {
                ["SodaCan"] = new VendingMachineInventoryEntry(InventoryType.Regular, "SodaCan", 7),
                ["Cigs"] = new VendingMachineInventoryEntry(InventoryType.Contraband, "Cigs", 0),
                ["Chocolate"] = new VendingMachineInventoryEntry(InventoryType.Emagged, "Chocolate", 42),
            };

            await server.WaitAssertion(() =>
            {
                var node = capture.TryCapture(original);
                Assert.That(node, Is.Not.Null,
                    "A Dictionary<string, VendingMachineInventoryEntry> must be capturable.");

                var restored = (Dictionary<string, VendingMachineInventoryEntry>)
                    capture.Restore(typeof(Dictionary<string, VendingMachineInventoryEntry>), node!)!;

                Assert.That(restored, Has.Count.EqualTo(original.Count));
                foreach (var (key, entry) in original)
                {
                    Assert.That(restored, Contains.Key(key));
                    var r = restored[key];
                    Assert.Multiple(() =>
                    {
                        Assert.That(r.Type, Is.EqualTo(entry.Type), $"{key} Type");
                        Assert.That(r.ID, Is.EqualTo(entry.ID), $"{key} ID");
                        Assert.That(r.Amount, Is.EqualTo(entry.Amount), $"{key} Amount");
                    });
                }
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TypeKeyedDict_IsUncapturable()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var serialization = server.ResolveDependency<ISerializationManager>();
            var capture = new ReflectiveStateCapture(serialization);

            // The StationRecords shape: Type-keyed, object-valued. Structurally
            // un-round-trippable -> capture must report it (null) so the caller strips it.
            var typeKeyed = new Dictionary<System.Type, Dictionary<uint, object>>
            {
                [typeof(int)] = new() { [1u] = new object() },
            };

            await server.WaitAssertion(() =>
            {
                Assert.That(capture.TryCapture(typeKeyed), Is.Null,
                    "A Type-keyed / object-valued dict is uncapturable and must return null (strip, not persist).");
            });

            await pair.CleanReturnAsync();
        }
    }
}
