using Colossal.Serialization.Entities;
using Game.Economy;
using Unity.Entities;

namespace WholesaleLogistics
{
    // One pending shop order queued on a WAREHOUSE (storage company) entity. Created by WholesaleBuyerSystem when
    // it intercepts a shop's vanilla purchase request; consumed by WholesaleDispatchSystem. Serialized so orders
    // survive save/load. The stop-list architecture (one buffer, ordered) is what stage A3 will extend into
    // multi-stop truck runs.
    public struct WholesaleOrder : IBufferElementData, ISerializable
    {
        public Entity m_Shop;
        public Resource m_Resource;
        public int m_Amount;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Shop);
            writer.Write(EconomyUtils.GetResourceIndex(m_Resource));
            writer.Write(m_Amount);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Shop);
            reader.Read(out int idx);
            m_Resource = EconomyUtils.GetResource(idx);
            reader.Read(out m_Amount);
        }
    }
}
