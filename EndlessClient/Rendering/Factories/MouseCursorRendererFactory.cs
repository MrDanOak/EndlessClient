﻿using AutomaticTypeMapper;
using EndlessClient.Controllers;
using EndlessClient.HUD;
using EOLib.Domain.Character;
using EOLib.Domain.Item;
using EOLib.Domain.Map;
using EOLib.Graphics;
using EOLib.IO.Repositories;

namespace EndlessClient.Rendering.Factories
{
    [AutoMappedType]
    public class MouseCursorRendererFactory : IMouseCursorRendererFactory
    {
        private readonly INativeGraphicsManager _nativeGraphicsManager;
        private readonly ICharacterProvider _characterProvider;
        private readonly IRenderOffsetCalculator _renderOffsetCalculator;
        private readonly IMapCellStateProvider _mapCellStateProvider;
        private readonly IItemStringService _itemStringService;
        private readonly IEIFFileProvider _eifFileProvider;
        private readonly ICurrentMapProvider _currentMapProvider;
        private readonly IGraphicsDeviceProvider _graphicsDeviceProvider;
        private readonly IMapInteractionController _mapInteractionController;
        private readonly IArrowKeyController _arrowKeyController;
        private readonly IPathFinder _pathFinder;

        public MouseCursorRendererFactory(INativeGraphicsManager nativeGraphicsManager,
                                          ICharacterProvider characterProvider,
                                          IRenderOffsetCalculator renderOffsetCalculator,
                                          IMapCellStateProvider mapCellStateProvider,
                                          IItemStringService itemStringService,
                                          IEIFFileProvider eifFileProvider,
                                          ICurrentMapProvider currentMapProvider,
                                          IGraphicsDeviceProvider graphicsDeviceProvider,
                                          IMapInteractionController mapInteractionController,
                                          IArrowKeyController arrowKeyController,
                                          IPathFinder pathFinder)
        {
            _nativeGraphicsManager = nativeGraphicsManager;
            _characterProvider = characterProvider;
            _renderOffsetCalculator = renderOffsetCalculator;
            _mapCellStateProvider = mapCellStateProvider;
            _itemStringService = itemStringService;
            _eifFileProvider = eifFileProvider;
            _currentMapProvider = currentMapProvider;
            _graphicsDeviceProvider = graphicsDeviceProvider;
            _mapInteractionController = mapInteractionController;
            _arrowKeyController = arrowKeyController;
            _pathFinder = pathFinder;
        }

        public IMouseCursorRenderer Create()
        {
            return new MouseCursorRenderer(_nativeGraphicsManager,
                                           _characterProvider,
                                           _renderOffsetCalculator,
                                           _mapCellStateProvider,
                                           _itemStringService,
                                           _eifFileProvider,
                                           _currentMapProvider,
                                           _graphicsDeviceProvider,
                                           _mapInteractionController,
                                           _arrowKeyController,
                                           _pathFinder);
        }
    }

    public interface IMouseCursorRendererFactory
    {
        IMouseCursorRenderer Create();
    }
}
