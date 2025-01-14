using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace Libplanet.Net.Protocols
{
    internal class RoutingTable
    {
        private readonly Address _address;
        private readonly int _tableSize;
        private readonly int _bucketSize;
        private readonly Random _random;
        private readonly KBucket[] _buckets;

        private readonly ILogger _logger;

        public RoutingTable(
            Address address,
            int tableSize,
            int bucketSize,
            Random random,
            ILogger logger)
        {
            if (tableSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tableSize));
            }

            if (bucketSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bucketSize));
            }

            _address = address;
            _tableSize = tableSize;
            _bucketSize = bucketSize;
            _random = random;
            _logger = logger;

            _buckets = new KBucket[tableSize];
            for (int i = 0; i < _tableSize; i++)
            {
                _buckets[i] = new KBucket(_bucketSize, _random, _logger);
            }
        }

        public int Count => _buckets.Sum(bucket => bucket.Count);

        public IEnumerable<BoundPeer> Peers => NonEmptyBuckets
            .SelectMany((bucket, _) => bucket.Peers).ToList();

        public IEnumerable<BoundPeer> PeersToBroadcast
        {
            get
            {
                return NonEmptyBuckets
                    .Select(bucket => bucket.GetRandomPeer());
            }
        }

        public IEnumerable<IEnumerable<BoundPeer>> CachesToCheck
        {
            get
            {
                return NonFullBuckets.Select(
                    bucket => bucket.ReplacementCache
                        .OrderBy(kv => kv.Value)
                        .Select(kv => kv.Key)
                );
            }
        }

        private IEnumerable<KBucket> NonFullBuckets
        {
            get
            {
                return _buckets.Where(bucket => !bucket.IsFull());
            }
        }

        private IEnumerable<KBucket> NonEmptyBuckets
        {
            get
            {
                return _buckets.Where(bucket => !bucket.IsEmpty());
            }
        }

        public IEnumerable<BoundPeer> PeersToRefresh(TimeSpan maxAge)
        {
            return NonEmptyBuckets
                .Where(bucket => bucket.Tail.Value + maxAge < DateTimeOffset.UtcNow)
                .Select(bucket => bucket.Tail.Key);
        }

        public void AddPeer(BoundPeer peer)
        {
            if (peer is null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            if (peer.Address.Equals(_address))
            {
                throw new ArgumentException("Cannot add self to routing table.");
            }

            _logger.Debug("Adding peer {Peer} to routing table.", peer);
            BucketOf(peer).AddPeer(peer);
        }

        public bool RemovePeer(BoundPeer peer)
        {
            if (peer is null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            if (peer.Address.Equals(_address))
            {
                throw new ArgumentException("Cannot remove self from routing table.");
            }

            return BucketOf(peer).RemovePeer(peer);
        }

        public bool RemoveCache(BoundPeer peer)
        {
            KBucket bucket = BucketOf(peer);
            return bucket.ReplacementCache.TryRemove(peer, out var dateTimeOffset);
        }

        public KBucket BucketOf(BoundPeer peer)
        {
            int index = GetBucketIndexOf(peer);
            return _buckets[index];
        }

        public KBucket BucketOf(int level)
        {
            return _buckets[level];
        }

        public bool Contains(BoundPeer peer)
        {
            return BucketOf(peer).Contains(peer);
        }

        public void Clear()
        {
            foreach (KBucket bucket in _buckets)
            {
                bucket.Clear();
            }
        }

        public IEnumerable<BoundPeer> Neighbors(Peer target, int k)
        {
            return Neighbors(target.Address, k);
        }

        // returns k nearest peers to given parameter peer from routing table.
        // return value is already sorted with respect to target.
        public IEnumerable<BoundPeer> Neighbors(Address target, int k)
        {
            var sorted = _buckets
                .Where(b => !b.IsEmpty())
                .SelectMany(b => b.Peers)
                .ToList();

            sorted = Kademlia.SortByDistance(sorted, target);
            var peers = new List<BoundPeer>();
            foreach (var peer in sorted.Where(peer => !peer.Address.Equals(target)))
            {
                peers.Add(peer);
                if (peers.Count >= k * 2)
                {
                    break;
                }
            }

            return peers;
        }

        private int GetBucketIndexOf(Peer peer)
        {
            int plength = Kademlia.CommonPrefixLength(peer.Address, _address);
            return Math.Min(plength, _tableSize - 1);
        }
    }
}
