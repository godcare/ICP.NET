﻿namespace ICP.Common.Candid
{
	public class CandidNull : CandidToken
	{
		public override CandidTokenType Type { get; } = CandidTokenType.Null;
	}
}