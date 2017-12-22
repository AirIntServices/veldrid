﻿using System;
using Vulkan;
using static Vulkan.VulkanNative;
using static Veldrid.Vk.VulkanUtil;
using System.Diagnostics;
using System.Collections.Generic;

namespace Veldrid.Vk
{
    internal unsafe class VkCommandList : CommandList
    {
        private readonly VkGraphicsDevice _gd;
        private VkCommandPool _pool;
        private VkCommandBuffer _cb;
        private bool _destroyed;

        private bool _commandBufferBegun;
        private bool _commandBufferEnded;
        private VkRect2D[] _scissorRects = Array.Empty<VkRect2D>();

        private VkClearValue[] _clearValues = Array.Empty<VkClearValue>();
        private bool[] _validColorClearValues = Array.Empty<bool>();
        private VkClearValue? _depthClearValue;

        // Graphics State
        private VkFramebufferBase _currentFramebuffer;
        private bool _currentFramebufferEverActive;
        private VkRenderPass _activeRenderPass;
        private VkPipeline _currentGraphicsPipeline;
        private VkResourceSet[] _currentGraphicsResourceSets = Array.Empty<VkResourceSet>();
        private bool[] _graphicsResourceSetsChanged;
        private int _newGraphicsResourceSets;

        // Compute State
        private VkPipeline _currentComputePipeline;
        private VkResourceSet[] _currentComputeResourceSets = Array.Empty<VkResourceSet>();
        private bool[] _computeResourceSetsChanged;
        private int _newComputeResourceSets;
        private string _name;

        public VkCommandPool CommandPool => _pool;
        public VkCommandBuffer CommandBuffer => _cb;

        public VkCommandList(VkGraphicsDevice gd, ref CommandListDescription description)
            : base(ref description)
        {
            _gd = gd;
            VkCommandPoolCreateInfo poolCI = VkCommandPoolCreateInfo.New();
            poolCI.queueFamilyIndex = gd.GraphicsQueueIndex;
            VkResult result = vkCreateCommandPool(_gd.Device, ref poolCI, null, out _pool);
            CheckResult(result);

            AllocateNewCommandBuffer();
        }

        private void AllocateNewCommandBuffer()
        {
            VkCommandBufferAllocateInfo cbAI = VkCommandBufferAllocateInfo.New();
            cbAI.commandPool = _pool;
            cbAI.commandBufferCount = 1;
            cbAI.level = VkCommandBufferLevel.Primary;
            VkResult result = vkAllocateCommandBuffers(_gd.Device, ref cbAI, out _cb);
            CheckResult(result);
        }

        public override void Begin()
        {
            if (_commandBufferBegun)
            {
                throw new VeldridException(
                    "CommandList must be in its initial state, or End() must have been called, for Begin() to be valid to call.");
            }
            if (_commandBufferEnded)
            {
                _commandBufferEnded = false;
                AllocateNewCommandBuffer();
            }

            _currentSubmissionInfo = GetNewSubmissionInfo();
            Debug.Assert(vkGetEventStatus(_gd.Device, _currentSubmissionInfo.CommandListEndEvent) == VkResult.EventReset);
            VkCommandBufferBeginInfo beginInfo = VkCommandBufferBeginInfo.New();
            beginInfo.flags = VkCommandBufferUsageFlags.OneTimeSubmit;
            vkBeginCommandBuffer(_cb, ref beginInfo);
            _commandBufferBegun = true;

            ClearCachedState();
            _currentFramebuffer = null;
            _currentGraphicsPipeline = null;
            Util.ClearArray(_currentGraphicsResourceSets);
            Util.ClearArray(_scissorRects);

            _currentComputePipeline = null;
            Util.ClearArray(_currentComputeResourceSets);

            _infoRemovalList.Clear();
            foreach (PooledStagingBufferInfo info in _usedStagingBuffers)
            {
                VkResult status = vkGetEventStatus(_gd.Device, info.AvailableEvent);
                if (status == VkResult.EventSet)
                {
                    vkResetEvent(_gd.Device, info.AvailableEvent);
                    _availableStagingBuffers.Add(info);
                    _infoRemovalList.Add(info);
                }
            }

            foreach (PooledStagingBufferInfo info in _infoRemovalList)
            {
                _usedStagingBuffers.Remove(info);
            }
        }

        private SubmittedResourceInfo GetNewSubmissionInfo()
        {
            if (_availableResourceInfos.Count > 0)
            {
                SubmittedResourceInfo ret = _availableResourceInfos[_availableResourceInfos.Count - 1];
                _availableResourceInfos.RemoveAt(_availableResourceInfos.Count - 1);
                return ret;
            }
            else if (_submittedResourceInfos.Count > 0)
            {
                FlushSubmittedResourceInfos();
                if (_availableResourceInfos.Count > 0)
                {
                    SubmittedResourceInfo ret = _availableResourceInfos[_availableResourceInfos.Count - 1];
                    _availableResourceInfos.RemoveAt(_availableResourceInfos.Count - 1);
                    return ret;
                }
            }

            return new SubmittedResourceInfo(_gd);
        }

        private void FlushSubmittedResourceInfos()
        {
            _submittedRemovalList.Clear();
            foreach (SubmittedResourceInfo info in _submittedResourceInfos)
            {
                if (vkGetEventStatus(_gd.Device, info.CommandListEndEvent) == VkResult.EventSet)
                {
                    foreach (VkDeferredDisposal resource in info.ReferencedResources)
                    {
                        resource.ReferenceTracker.Decrement();
                    }
                    vkResetEvent(_gd.Device, info.CommandListEndEvent);

                    _availableResourceInfos.Add(info);
                    _submittedRemovalList.Add(info);
                }
            }

            foreach (SubmittedResourceInfo info in _submittedRemovalList)
            {
                _submittedResourceInfos.Remove(info);
                info.ReferencedResources.Clear();
            }
        }

        protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
        {
            VkClearValue clearValue = new VkClearValue
            {
                color = new VkClearColorValue(clearColor.R, clearColor.G, clearColor.B, clearColor.A)
            };

            if (_activeRenderPass != VkRenderPass.Null)
            {
                VkClearAttachment clearAttachment = new VkClearAttachment
                {
                    colorAttachment = index,
                    aspectMask = VkImageAspectFlags.Color,
                    clearValue = clearValue
                };

                Texture colorTex = _currentFramebuffer.ColorTargets[(int)index].Target;
                VkClearRect clearRect = new VkClearRect
                {
                    baseArrayLayer = 0,
                    layerCount = 1,
                    rect = new VkRect2D(0, 0, colorTex.Width, colorTex.Height)
                };

                vkCmdClearAttachments(_cb, 1, ref clearAttachment, 1, ref clearRect);
            }
            else
            {
                // Queue up the clear value for the next RenderPass.
                _clearValues[index] = clearValue;
                _validColorClearValues[index] = true;
            }
        }

        protected override void ClearDepthStencilCore(float depth, byte stencil)
        {
            VkClearValue clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(depth, stencil) };

            if (_activeRenderPass != VkRenderPass.Null)
            {
                VkClearAttachment clearAttachment = new VkClearAttachment
                {
                    aspectMask = VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil,
                    clearValue = clearValue
                };

                Texture depthTex = _currentFramebuffer.DepthTarget.Value.Target;
                VkClearRect clearRect = new VkClearRect
                {
                    baseArrayLayer = 0,
                    layerCount = 1,
                    rect = new VkRect2D(0, 0, depthTex.Width, depthTex.Height)
                };

                vkCmdClearAttachments(_cb, 1, ref clearAttachment, 1, ref clearRect);
            }
            else
            {
                // Queue up the clear value for the next RenderPass.
                _depthClearValue = clearValue;
            }
        }

        protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            PreDrawCommand();
            vkCmdDraw(_cb, vertexCount, instanceCount, vertexStart, instanceStart);
        }

        internal void OnSubmitted()
        {
            Debug.Assert(!_submittedResourceInfos.Contains(_currentSubmissionInfo));
            _submittedResourceInfos.Add(_currentSubmissionInfo);

            foreach (VkDeferredDisposal resource in _currentSubmissionInfo.ReferencedResources)
            {
                resource.ReferenceTracker.Increment();
            }

            _currentSubmissionInfo = null;
        }

        protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            PreDrawCommand();
            vkCmdDrawIndexed(_cb, indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        }

        protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            PreDrawCommand();
            VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
            vkCmdDrawIndirect(_cb, vkBuffer.DeviceBuffer, offset, drawCount, stride);
            AddReference(vkBuffer);
        }

        protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
        {
            PreDrawCommand();
            VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
            vkCmdDrawIndexedIndirect(_cb, vkBuffer.DeviceBuffer, offset, drawCount, stride);
            AddReference(vkBuffer);
        }

        private void PreDrawCommand()
        {
            EnsureRenderPassActive();

            FlushNewResourceSets(
                _newGraphicsResourceSets,
                _currentGraphicsResourceSets,
                _graphicsResourceSetsChanged,
                VkPipelineBindPoint.Graphics,
                _currentGraphicsPipeline.PipelineLayout);
            _newGraphicsResourceSets = 0;

            if (!_currentGraphicsPipeline.ScissorTestEnabled)
            {
                SetFullScissorRects();
            }
        }

        private void FlushNewResourceSets(
            int newResourceSetsCount,
            VkResourceSet[] resourceSets,
            bool[] resourceSetsChanged,
            VkPipelineBindPoint bindPoint,
            VkPipelineLayout pipelineLayout)
        {
            if (newResourceSetsCount > 0)
            {
                int totalChanged = 0;
                uint currentSlot = 0;
                uint currentBatchIndex = 0;
                uint currentBatchFirstSet = 0;
                VkDescriptorSet* descriptorSets = stackalloc VkDescriptorSet[newResourceSetsCount];
                while (totalChanged < newResourceSetsCount)
                {
                    if (resourceSetsChanged[currentSlot])
                    {
                        resourceSetsChanged[currentSlot] = false;
                        descriptorSets[currentBatchIndex] = resourceSets[currentSlot].DescriptorSet;
                        totalChanged += 1;
                        currentBatchIndex += 1;
                        currentSlot += 1;
                    }
                    else
                    {
                        if (currentBatchIndex != 0)
                        {
                            // Flush current batch.
                            vkCmdBindDescriptorSets(
                                _cb,
                                bindPoint,
                                pipelineLayout,
                                currentBatchFirstSet,
                                currentBatchIndex,
                                descriptorSets,
                                0,
                                null);
                            currentBatchIndex = 0;
                        }

                        currentSlot += 1;
                        currentBatchFirstSet = currentSlot;
                    }
                }

                if (currentBatchIndex != 0)
                {
                    // Flush current batch.
                    vkCmdBindDescriptorSets(
                        _cb,
                        bindPoint,
                        pipelineLayout,
                        currentBatchFirstSet,
                        currentBatchIndex,
                        descriptorSets,
                        0,
                        null);
                }
            }
        }

        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            PreDispatchCommand();

            vkCmdDispatch(_cb, groupCountX, groupCountY, groupCountZ);
        }

        private void PreDispatchCommand()
        {
            EnsureNoRenderPass();

            FlushNewResourceSets(
                _newComputeResourceSets,
                _currentComputeResourceSets,
                _computeResourceSetsChanged,
                VkPipelineBindPoint.Compute,
                _currentComputePipeline.PipelineLayout);
            _newComputeResourceSets = 0;
        }

        protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
        {
            PreDispatchCommand();

            VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(indirectBuffer);
            vkCmdDispatchIndirect(_cb, vkBuffer.DeviceBuffer, offset);
            AddReference(vkBuffer);
        }

        private void AddReference(VkDeferredDisposal vdd)
        {
            _currentSubmissionInfo.ReferencedResources.Add(vdd);
        }

        protected override void ResolveTextureCore(Texture source, Texture destination)
        {
            if (_activeRenderPass != VkRenderPass.Null)
            {
                EndCurrentRenderPass();
            }

            VkTexture vkSource = Util.AssertSubtype<Texture, VkTexture>(source);
            VkTexture vkDestination = Util.AssertSubtype<Texture, VkTexture>(destination);
            VkImageAspectFlags aspectFlags = ((source.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil)
                ? VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil
                : VkImageAspectFlags.Color;
            VkImageResolve region = new VkImageResolve
            {
                extent = new VkExtent3D { width = source.Width, height = source.Height, depth = source.Depth },
                srcSubresource = new VkImageSubresourceLayers { layerCount = 1, aspectMask = aspectFlags },
                dstSubresource = new VkImageSubresourceLayers { layerCount = 1, aspectMask = aspectFlags }
            };

            vkCmdResolveImage(
                _cb,
                vkSource.OptimalDeviceImage,
                vkSource.GetImageLayout(0, 0),
                vkDestination.OptimalDeviceImage,
                vkDestination.GetImageLayout(0, 0),
                1,
                ref region);

            AddReference(vkSource);
            AddReference(vkDestination);
        }

        public override void End()
        {
            if (!_commandBufferBegun)
            {
                throw new VeldridException("CommandBuffer must have been started before End() may be called.");
            }

            _commandBufferBegun = false;
            _commandBufferEnded = true;

            if (_activeRenderPass != VkRenderPass.Null)
            {
                EndCurrentRenderPass();
            }
            else if (!_currentFramebufferEverActive && _currentFramebuffer != null)
            {
                BeginCurrentRenderPass();
                EndCurrentRenderPass();
            }

            vkCmdSetEvent(_cb, _currentSubmissionInfo.CommandListEndEvent, VkPipelineStageFlags.AllCommands);

            vkEndCommandBuffer(_cb);
        }

        protected override void SetFramebufferCore(Framebuffer fb)
        {
            if (_activeRenderPass.Handle != VkRenderPass.Null)
            {
                EndCurrentRenderPass();
                // Place a barrier between RenderPasses, so that color / depth outputs
                // can be read in subsequent passes.
                vkCmdPipelineBarrier(
                    _cb,
                    VkPipelineStageFlags.ColorAttachmentOutput,
                    VkPipelineStageFlags.TopOfPipe,
                    VkDependencyFlags.ByRegion,
                    0,
                    null,
                    0,
                    null,
                    0,
                    null);
            }

            VkFramebufferBase vkFB = Util.AssertSubtype<Framebuffer, VkFramebufferBase>(fb);
            _currentFramebuffer = vkFB;
            _currentFramebufferEverActive = false;
            Util.EnsureArrayMinimumSize(ref _scissorRects, Math.Max(1, (uint)vkFB.ColorTargets.Count));
            uint clearValueCount = (uint)vkFB.ColorTargets.Count;
            Util.EnsureArrayMinimumSize(ref _clearValues, clearValueCount + 1); // Leave an extra space for the depth value (tracked separately).
            Util.ClearArray(_validColorClearValues);
            Util.EnsureArrayMinimumSize(ref _validColorClearValues, clearValueCount);
            AddReference(vkFB);
        }

        private void EnsureRenderPassActive()
        {
            if (_activeRenderPass == VkRenderPass.Null)
            {
                BeginCurrentRenderPass();
            }
        }

        private void EnsureNoRenderPass()
        {
            if (_activeRenderPass != VkRenderPass.Null)
            {
                EndCurrentRenderPass();
            }
        }

        private void BeginCurrentRenderPass()
        {
            Debug.Assert(_activeRenderPass == VkRenderPass.Null);
            Debug.Assert(_currentFramebuffer != null);
            _currentFramebufferEverActive = true;

            uint attachmentCount = _currentFramebuffer.AttachmentCount;
            bool haveAnyAttachments = _framebuffer.ColorTargets.Count > 0 || _framebuffer.DepthTarget != null;
            bool haveAllClearValues = _depthClearValue.HasValue || _framebuffer.DepthTarget == null;
            bool haveAnyClearValues = _depthClearValue.HasValue;
            for (int i = 0; i < _currentFramebuffer.ColorTargets.Count; i++)
            {
                if (!_validColorClearValues[i])
                {
                    haveAllClearValues = false;
                    haveAnyClearValues = true;
                }
                else
                {
                    haveAnyClearValues = true;
                }
            }

            VkRenderPassBeginInfo renderPassBI = VkRenderPassBeginInfo.New();
            renderPassBI.renderArea = new VkRect2D(_currentFramebuffer.RenderableWidth, _currentFramebuffer.RenderableHeight);
            renderPassBI.framebuffer = _currentFramebuffer.CurrentFramebuffer;

            if (!haveAnyAttachments || !haveAllClearValues)
            {
                renderPassBI.renderPass = _currentFramebuffer.RenderPassNoClear;
                vkCmdBeginRenderPass(_cb, ref renderPassBI, VkSubpassContents.Inline);
                _activeRenderPass = _currentFramebuffer.RenderPassNoClear;

                if (haveAnyClearValues)
                {
                    if (_depthClearValue.HasValue)
                    {
                        ClearDepthStencil(_depthClearValue.Value.depthStencil.depth, (byte)_depthClearValue.Value.depthStencil.stencil);
                        _depthClearValue = null;
                    }

                    for (uint i = 0; i < _currentFramebuffer.ColorTargets.Count; i++)
                    {
                        if (_validColorClearValues[i])
                        {
                            _validColorClearValues[i] = false;
                            VkClearValue vkClearValue = _clearValues[i];
                            RgbaFloat clearColor = new RgbaFloat(
                                vkClearValue.color.float32_0,
                                vkClearValue.color.float32_1,
                                vkClearValue.color.float32_2,
                                vkClearValue.color.float32_3);
                            ClearColorTarget(i, clearColor);
                        }
                    }
                }
            }
            else
            {
                // We have clear values for every attachment.
                renderPassBI.renderPass = _currentFramebuffer.RenderPassClear;
                fixed (VkClearValue* clearValuesPtr = &_clearValues[0])
                {
                    renderPassBI.clearValueCount = attachmentCount;
                    renderPassBI.pClearValues = clearValuesPtr;
                    if (_depthClearValue.HasValue)
                    {
                        _clearValues[_currentFramebuffer.ColorTargets.Count] = _depthClearValue.Value;
                    }
                    vkCmdBeginRenderPass(_cb, ref renderPassBI, VkSubpassContents.Inline);
                    _activeRenderPass = _currentFramebuffer.RenderPassClear;
                    Util.ClearArray(_validColorClearValues);
                }
            }
        }

        private void EndCurrentRenderPass()
        {
            Debug.Assert(_activeRenderPass != VkRenderPass.Null);
            vkCmdEndRenderPass(_cb);
            _activeRenderPass = VkRenderPass.Null;
        }

        protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer)
        {
            VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(buffer);
            Vulkan.VkBuffer deviceBuffer = vkBuffer.DeviceBuffer;
            ulong offset = 0;
            vkCmdBindVertexBuffers(_cb, index, 1, ref deviceBuffer, ref offset);
            AddReference(vkBuffer);
        }

        protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format)
        {
            VkBuffer vkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(buffer);
            vkCmdBindIndexBuffer(_cb, vkBuffer.DeviceBuffer, 0, VkFormats.VdToVkIndexFormat(format));
            AddReference(vkBuffer);
        }

        protected override void SetPipelineCore(Pipeline pipeline)
        {
            if (!pipeline.IsComputePipeline && _currentGraphicsPipeline != pipeline)
            {
                VkPipeline vkPipeline = Util.AssertSubtype<Pipeline, VkPipeline>(pipeline);
                Util.EnsureArrayMinimumSize(ref _currentGraphicsResourceSets, vkPipeline.ResourceSetCount);
                Util.ClearArray(_currentGraphicsResourceSets);
                Util.EnsureArrayMinimumSize(ref _graphicsResourceSetsChanged, vkPipeline.ResourceSetCount);
                vkCmdBindPipeline(_cb, VkPipelineBindPoint.Graphics, vkPipeline.DevicePipeline);
                _currentGraphicsPipeline = vkPipeline;
                AddReference(vkPipeline);
            }
            else if (pipeline.IsComputePipeline && _currentComputePipeline != pipeline)
            {
                VkPipeline vkPipeline = Util.AssertSubtype<Pipeline, VkPipeline>(pipeline);
                Util.EnsureArrayMinimumSize(ref _currentComputeResourceSets, vkPipeline.ResourceSetCount);
                Util.ClearArray(_currentComputeResourceSets);
                Util.EnsureArrayMinimumSize(ref _computeResourceSetsChanged, vkPipeline.ResourceSetCount);
                vkCmdBindPipeline(_cb, VkPipelineBindPoint.Compute, vkPipeline.DevicePipeline);
                _currentComputePipeline = vkPipeline;
                AddReference(vkPipeline);
            }
        }

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs)
        {
            if (_currentGraphicsResourceSets[slot] != rs)
            {
                VkResourceSet vkRS = Util.AssertSubtype<ResourceSet, VkResourceSet>(rs);
                _currentGraphicsResourceSets[slot] = vkRS;
                _graphicsResourceSetsChanged[slot] = true;
                _newGraphicsResourceSets += 1;
                AddReference(vkRS);
            }
        }

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet rs)
        {
            if (_currentComputeResourceSets[slot] != rs)
            {
                VkResourceSet vkRS = Util.AssertSubtype<ResourceSet, VkResourceSet>(rs);
                _currentComputeResourceSets[slot] = vkRS;
                _computeResourceSetsChanged[slot] = true;
                _newComputeResourceSets += 1;
                AddReference(vkRS);
            }
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            VkRect2D scissor = new VkRect2D((int)x, (int)y, (int)width, (int)height);
            if (_scissorRects[index] != scissor)
            {
                _scissorRects[index] = scissor;
                vkCmdSetScissor(_cb, index, 1, ref scissor);
            }
        }

        public override void SetViewport(uint index, ref Viewport viewport)
        {
            VkViewport vkViewport = new VkViewport
            {
                x = viewport.X,
                y = viewport.Y,
                width = viewport.Width,
                height = viewport.Height,
                minDepth = viewport.MinDepth,
                maxDepth = viewport.MaxDepth
            };

            vkCmdSetViewport(_cb, index, 1, ref vkViewport);
        }

        public override void UpdateBuffer(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            PooledStagingBufferInfo stagingBufferInfo = GetStagingBuffer(sizeInBytes);
            _gd.UpdateBuffer(stagingBufferInfo.Buffer, 0, source, sizeInBytes);
            CopyBuffer(stagingBufferInfo.Buffer, 0, buffer, bufferOffsetInBytes, sizeInBytes);
            vkCmdSetEvent(_cb, stagingBufferInfo.AvailableEvent, VkPipelineStageFlags.Transfer);
        }

        protected override void CopyBufferCore(
            DeviceBuffer source,
            uint sourceOffset,
            DeviceBuffer destination,
            uint destinationOffset,
            uint sizeInBytes)
        {
            EnsureNoRenderPass();

            VkBuffer srcVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(source);
            VkBuffer dstVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(destination);

            VkBufferCopy region = new VkBufferCopy
            {
                srcOffset = sourceOffset,
                dstOffset = destinationOffset,
                size = sizeInBytes
            };

            vkCmdCopyBuffer(_cb, srcVkBuffer.DeviceBuffer, dstVkBuffer.DeviceBuffer, 1, ref region);

            AddReference(srcVkBuffer);
            AddReference(dstVkBuffer);
        }

        protected override void CopyTextureCore(
            Texture source,
            uint srcX, uint srcY, uint srcZ,
            uint srcMipLevel,
            uint srcBaseArrayLayer,
            Texture destination,
            uint dstX, uint dstY, uint dstZ,
            uint dstMipLevel,
            uint dstBaseArrayLayer,
            uint width, uint height, uint depth,
            uint layerCount)
        {
            EnsureNoRenderPass();

            bool sourceIsStaging = (source.Usage & TextureUsage.Staging) == TextureUsage.Staging;
            bool destIsStaging = (destination.Usage & TextureUsage.Staging) == TextureUsage.Staging;
            if ((destIsStaging || sourceIsStaging) && layerCount > 1)
            {
                // Need to issue one copy per array layer.
                throw new NotImplementedException();
            }

            VkImageSubresourceLayers srcSubresource = new VkImageSubresourceLayers
            {
                aspectMask = VkImageAspectFlags.Color,
                layerCount = layerCount,
                mipLevel = sourceIsStaging ? 0 : srcMipLevel,
                baseArrayLayer = sourceIsStaging ? 0 : srcBaseArrayLayer
            };

            VkImageSubresourceLayers dstSubresource = new VkImageSubresourceLayers
            {
                aspectMask = VkImageAspectFlags.Color,
                layerCount = layerCount,
                mipLevel = destIsStaging ? 0 : dstMipLevel,
                baseArrayLayer = destIsStaging ? 0 : dstBaseArrayLayer
            };

            VkImageCopy region = new VkImageCopy
            {
                srcOffset = new VkOffset3D { x = (int)srcX, y = (int)srcY, z = (int)srcZ },
                dstOffset = new VkOffset3D { x = (int)dstX, y = (int)dstY, z = (int)dstZ },
                srcSubresource = srcSubresource,
                dstSubresource = dstSubresource,
                extent = new VkExtent3D { width = width, height = height, depth = depth }
            };

            VkTexture srcVkTexture = Util.AssertSubtype<Texture, VkTexture>(source);
            VkTexture dstVkTexture = Util.AssertSubtype<Texture, VkTexture>(destination);

            srcVkTexture.TransitionImageLayout(
                _cb,
                srcMipLevel,
                1,
                srcBaseArrayLayer,
                layerCount,
                VkImageLayout.TransferSrcOptimal);

            dstVkTexture.TransitionImageLayout(
                _cb,
                dstMipLevel,
                1,
                dstBaseArrayLayer,
                layerCount,
                VkImageLayout.TransferDstOptimal);

            VkImage srcImage = sourceIsStaging
                ? srcVkTexture.GetStagingImage(source.CalculateSubresource(srcMipLevel, srcBaseArrayLayer))
                : srcVkTexture.OptimalDeviceImage;
            VkImage dstImage = destIsStaging
                ? dstVkTexture.GetStagingImage(destination.CalculateSubresource(dstMipLevel, dstBaseArrayLayer))
                : dstVkTexture.OptimalDeviceImage;

            vkCmdCopyImage(
                _cb,
                srcImage,
                VkImageLayout.TransferSrcOptimal,
                dstImage,
                VkImageLayout.TransferDstOptimal,
                1,
                ref region);

            AddReference(srcVkTexture);
            AddReference(dstVkTexture);
        }

        public override string Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetResourceName(this, value);
            }
        }

        private PooledStagingBufferInfo GetStagingBuffer(uint size)
        {
            foreach (PooledStagingBufferInfo info in _availableStagingBuffers)
            {
                if (info.Buffer.SizeInBytes > size)
                {
                    _availableStagingBuffers.Remove(info);
                    _usedStagingBuffers.Add(info);
                    return info;
                }
            }

            VkBuffer newBuffer = (VkBuffer)_gd.ResourceFactory.CreateBuffer(new BufferDescription(size, BufferUsage.Staging));
            PooledStagingBufferInfo newInfo = new PooledStagingBufferInfo(_gd, newBuffer);
            _usedStagingBuffers.Add(newInfo);
            return newInfo;
        }

        public override void Dispose()
        {
            _gd.EnqueueDisposedCommandBuffer(this);
        }

        // Must only be called once the command buffer has fully executed.
        public void DestroyCommandPool()
        {
            if (!_destroyed)
            {
                _destroyed = true;
                vkDestroyCommandPool(_gd.Device, _pool, null);

                foreach (var info in _availableStagingBuffers)
                {
                    info.Buffer.Dispose();
                    vkDestroyEvent(_gd.Device, info.AvailableEvent, null);
                }
                foreach (var info in _usedStagingBuffers)
                {
                    info.Buffer.Dispose();
                    vkDestroyEvent(_gd.Device, info.AvailableEvent, null);
                }

                FlushSubmittedResourceInfos();
                Debug.Assert(_submittedResourceInfos.Count == 0);
                foreach (SubmittedResourceInfo info in _availableResourceInfos)
                {
                    vkDestroyEvent(_gd.Device, info.CommandListEndEvent, null);
                }
            }
        }

        private readonly List<PooledStagingBufferInfo> _infoRemovalList = new List<PooledStagingBufferInfo>();
        private readonly List<PooledStagingBufferInfo> _usedStagingBuffers = new List<PooledStagingBufferInfo>();
        private readonly List<PooledStagingBufferInfo> _availableStagingBuffers = new List<PooledStagingBufferInfo>();

        private SubmittedResourceInfo _currentSubmissionInfo;
        private readonly List<SubmittedResourceInfo> _submittedRemovalList = new List<SubmittedResourceInfo>();
        private readonly List<SubmittedResourceInfo> _submittedResourceInfos = new List<SubmittedResourceInfo>();
        private readonly List<SubmittedResourceInfo> _availableResourceInfos = new List<SubmittedResourceInfo>();

        private class SubmittedResourceInfo
        {
            public readonly VkEvent CommandListEndEvent;
            public readonly HashSet<VkDeferredDisposal> ReferencedResources = new HashSet<VkDeferredDisposal>();

            public SubmittedResourceInfo(VkGraphicsDevice gd)
            {
                VkEventCreateInfo eventCI = VkEventCreateInfo.New();
                VkResult result = vkCreateEvent(gd.Device, ref eventCI, null, out CommandListEndEvent);
                CheckResult(result);
            }
        }

        private class PooledStagingBufferInfo
        {
            public readonly VkEvent AvailableEvent;
            public readonly VkBuffer Buffer;

            public PooledStagingBufferInfo(VkGraphicsDevice gd, VkBuffer buffer)
            {
                VkEventCreateInfo eventCI = VkEventCreateInfo.New();
                VkResult result = vkCreateEvent(gd.Device, ref eventCI, null, out AvailableEvent);
                CheckResult(result);

                Buffer = buffer;
            }
        }
    }
}