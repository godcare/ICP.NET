﻿using EdjCase.ICP.Candid.Encodings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EdjCase.ICP.Candid.Models.Types
{
	public class CandidReferenceType : CandidType
	{
		public CandidId Id { get; }

		public CandidReferenceType(CandidId id)
		{
			this.Id = id;
		}

		public override byte[] Encode(CompoundTypeTable compoundTypeTable)
		{
			uint index = compoundTypeTable.GetRecursiveReferenceIndex(this.Id);
			return LEB128.EncodeUnsigned(index);
		}

		public override bool Equals(object? obj)
		{
			if (obj is CandidReferenceType r)
			{
				return this.Id == r.Id;
			}
			if (obj is CandidCompoundType c)
			{
				return this.Id == c.RecursiveId;
			}
			return false;
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(this.Id);
		}
	}
}
