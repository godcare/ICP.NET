using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EdjCase.ICP.Candid.Crypto;

namespace EdjCase.ICP.Candid.Models
{
	public interface IHashable
	{
		byte[] ComputeHash(IHashFunction hashFunction);
	}

	public interface IRepresentationIndependentHashItem
	{
		Dictionary<string, IHashable> BuildHashableItem();
	}

	public class HashableObject : IHashable, IEnumerable<KeyValuePair<string, IHashable>>
	{
		public Dictionary<string, IHashable> Properties { get; }

		public HashableObject(Dictionary<string, IHashable>? properties = null)
		{
			this.Properties = properties ?? new Dictionary<string, IHashable>();
		}

		public void Add(string key, IHashable value)
		{
			this.Add(key.ToHashable(), value);
		}

		public byte[] ComputeHash(IHashFunction hashFunction)
		{
			byte[] requestIdBytes = this.Properties
				.Select(o =>
				{
					byte[] keyDigest = o.Key.ToHashable().ComputeHash(hashFunction);
					byte[] valueDigest = o.Value.ComputeHash(hashFunction);

					return (Key: keyDigest, Value: valueDigest);
				}) // Hash key and value bytes
				.Where(o => o.Value != null) // Remove empty/null ones
				.OrderBy(o => o.Key, new HashComparer()) // Keys in order
				.SelectMany(o => o.Key.Concat(o.Value))
				.ToArray(); // Create single byte[] by concatinating them all together
			return hashFunction.ComputeHash(requestIdBytes); // Hash concatinated bytes
		}

		public IEnumerator<KeyValuePair<string, IHashable>> GetEnumerator()
		{
			return this.Properties.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return this.Properties.GetEnumerator();
		}
	}

	internal class HashComparer : IComparer<byte[]>
	{
		public int Compare(byte[] x, byte[] y)
		{
			if (x.Length != y.Length)
			{
				return x.Length - y.Length;
			}
			for(int i = 0; i < x.Length; i++)
			{
				if(x[i] != y[i])
				{
					return x[i] - y[i];
				}
			}
			return 0;
		}
	}

	public class HashableArray : IHashable
	{
		public HashableArray(List<IHashable>? items = null)
		{
			this.Items = items ?? new List<IHashable>();
		}

		public List<IHashable> Items { get; }

		public byte[] ComputeHash(IHashFunction hashFunction)
		{
			return this.Items
				.SelectMany(i => i.ComputeHash(hashFunction))
				.ToArray();
		}
	}

	public abstract class HashableValue : IHashable
	{
		public abstract byte[] GetRawValue();

		public byte[] ComputeHash(IHashFunction hashFunction)
		{
			byte[] raw = this.GetRawValue();
			return hashFunction.ComputeHash(raw);
		}
	}

	public class HashableString : HashableValue
	{
		public string Value { get; }

		public HashableString(string value)
		{
			this.Value = value ?? throw new ArgumentNullException(nameof(value));
		}

		public static implicit operator HashableString(string value)
		{
			return new HashableString(value);
		}

		public static implicit operator string(HashableString value)
		{
			return value.Value;
		}

		public override byte[] GetRawValue()
		{
			return Encoding.UTF8.GetBytes(this.Value);
		}
	}

	public class HashableBytes : HashableValue
	{
		public byte[] Value { get; }

		public HashableBytes(byte[] value)
		{
			this.Value = value ?? throw new ArgumentNullException(nameof(value));
		}

		public override byte[] GetRawValue()
		{
			return this.Value;
		}
	}



	public static class HashableExtensions
	{
		public static HashableArray ToHashable<T>(this IEnumerable<T> items)
			where T : IHashable
		{
			return HashableExtensions.ToHashable<T>(items, i => i);
		}

		public static HashableArray ToHashable<T>(this IEnumerable<T> items, Func<T, IHashable> converter)
		{
			return new HashableArray(items.Select(converter).ToList());
		}

		public static HashableString ToHashable(this string value)
		{
			return new HashableString(value);
		}

		public static HashableBytes ToHashable(this byte[] value)
		{
			return new HashableBytes(value);
		}

		public static HashableObject ToHashable<T>(this Dictionary<string, T> value)
			where T : IHashable
		{
			return new HashableObject(value.ToDictionary(v => v.Key, v => (IHashable)v.Value));
		}
	}
}