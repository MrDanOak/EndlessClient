﻿using AutomaticTypeMapper;
using EOLib.Graphics;
using Microsoft.Xna.Framework.Graphics;

namespace EndlessClient.Rendering.Factories
{
    [MappedType(BaseType = typeof(IRenderTargetFactory))]
    public class RenderTargetFactory : IRenderTargetFactory
    {
        private readonly IGraphicsDeviceProvider _graphicsDeviceProvider;
        private readonly IClientWindowSizeProvider _clientWindowSizeProvider;

        public RenderTargetFactory(IGraphicsDeviceProvider graphicsDeviceProvider,
                                      IClientWindowSizeProvider clientWindowSizeProvider)
        {
            _graphicsDeviceProvider = graphicsDeviceProvider;
            _clientWindowSizeProvider = clientWindowSizeProvider;
        }

        public RenderTarget2D CreateRenderTarget()
        {
            return new RenderTarget2D(
                _graphicsDeviceProvider.GraphicsDevice,
                _clientWindowSizeProvider.Width,
                _clientWindowSizeProvider.Height,
                false,
                SurfaceFormat.Color,
                DepthFormat.None);
        }
    }
}
