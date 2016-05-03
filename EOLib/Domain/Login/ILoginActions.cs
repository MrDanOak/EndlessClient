﻿// Original Work Copyright (c) Ethan Moffat 2014-2016
// This file is subject to the GPL v2 License
// For additional details, see the LICENSE file

using System.Threading.Tasks;
using EOLib.Domain.BLL;

namespace EOLib.Domain.Login
{
	public interface ILoginActions
	{
		bool LoginParametersAreValid(ILoginParameters parameters);

		Task<LoginReply> LoginToServer(ILoginParameters parameters);

		Task<ILoginRequestGrantedData> RequestCharacterLogin(ICharacter character);

		Task<ILoginRequestCompletedData> CompleteCharacterLogin(ICharacter character);
	}
}
