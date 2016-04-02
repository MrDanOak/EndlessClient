﻿// Original Work Copyright (c) Ethan Moffat 2014-2016
// This file is subject to the GPL v2 License
// For additional details, see the LICENSE file

using System;
using System.Collections.Generic;
using System.Linq;
using EndlessClient.ControlSets;
using Microsoft.Xna.Framework;
using XNAControls;

namespace EndlessClient.Game
{
	public class GameStateActions : IGameStateActions
	{
		private readonly IGameStateRepository _gameStateRepository;
		private readonly IControlSetRepository _controlSetRepository;
		private readonly IControlSetFactory _controlSetFactory;
		private readonly IEndlessGame _endlessGame;

		public GameStateActions(IGameStateRepository gameStateRepository,
								IControlSetRepository controlSetRepository,
								IControlSetFactory controlSetFactory,
								IEndlessGame endlessGame)
		{
			_gameStateRepository = gameStateRepository;
			_controlSetRepository = controlSetRepository;
			_controlSetFactory = controlSetFactory;
			_endlessGame = endlessGame;
		}

		public void ChangeToState(GameStates newState)
		{
			if (newState == _gameStateRepository.CurrentState)
				return;

			var currentSet = _controlSetRepository.CurrentControlSet;
			var nextSet = _controlSetFactory.CreateControlsForState(newState, currentSet);

			var componentsToRemove = FindUnusedComponents(currentSet, nextSet);
			var xnaControlComponents = componentsToRemove.OfType<XNAControl>().ToList();
			var otherDisposableComponents = componentsToRemove.Except(xnaControlComponents).OfType<IDisposable>();

			foreach (var component in xnaControlComponents)
				component.Close();
			foreach (var component in otherDisposableComponents)
				component.Dispose();
			foreach (var component in nextSet.AllComponents)
				if (!_endlessGame.Components.Contains(component))
					_endlessGame.Components.Add(component);

			_gameStateRepository.CurrentState = newState;
			_controlSetRepository.CurrentControlSet = nextSet;
		}

		private List<IGameComponent> FindUnusedComponents(IControlSet current, IControlSet next)
		{
			return current.AllComponents
				.Where(component => !next.AllComponents.Contains(component))
				.ToList();
		}
	}
}
