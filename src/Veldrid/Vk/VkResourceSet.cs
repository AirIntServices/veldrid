﻿using Vulkan;
using static Vulkan.VulkanNative;
using static Veldrid.Vk.VulkanUtil;

namespace Veldrid.Vk
{
    internal unsafe class VkResourceSet : ResourceSet, VkDeferredDisposal
    {
        private readonly VkGraphicsDevice _gd;
        private readonly DescriptorResourceCounts _descriptorCounts;
        private readonly DescriptorAllocationToken _descriptorAllocationToken;
        private readonly BindableResource[] _boundResources;
        private bool _disposed;
        private string _name;

        public VkDescriptorSet DescriptorSet => _descriptorAllocationToken.Set;

        public ReferenceTracker ReferenceTracker { get; } = new ReferenceTracker();

        public VkResourceSet(VkGraphicsDevice gd, ref ResourceSetDescription description)
            : base(ref description)
        {
            _gd = gd;
            VkResourceLayout vkLayout = Util.AssertSubtype<ResourceLayout, VkResourceLayout>(description.Layout);

            VkDescriptorSetLayout dsl = vkLayout.DescriptorSetLayout;
            _descriptorCounts = vkLayout.DescriptorResourceCounts;
            _descriptorAllocationToken = _gd.DescriptorPoolManager.Allocate(_descriptorCounts, dsl);

            _boundResources = description.BoundResources;
            uint descriptorWriteCount = (uint)_boundResources.Length;
            VkWriteDescriptorSet* descriptorWrites = stackalloc VkWriteDescriptorSet[(int)descriptorWriteCount];
            VkDescriptorBufferInfo* bufferInfos = stackalloc VkDescriptorBufferInfo[(int)descriptorWriteCount];
            VkDescriptorImageInfo* imageInfos = stackalloc VkDescriptorImageInfo[(int)descriptorWriteCount];

            for (int i = 0; i < descriptorWriteCount; i++)
            {
                VkDescriptorType type = vkLayout.DescriptorTypes[i];

                descriptorWrites[i].sType = VkStructureType.WriteDescriptorSet;
                descriptorWrites[i].descriptorCount = 1;
                descriptorWrites[i].descriptorType = type;
                descriptorWrites[i].dstBinding = (uint)i;
                descriptorWrites[i].dstSet = _descriptorAllocationToken.Set;

                if (type == VkDescriptorType.UniformBuffer || type == VkDescriptorType.StorageBuffer)
                {
                    VkBuffer vkBuffer = Util.AssertSubtype<BindableResource, VkBuffer>(_boundResources[i]);
                    bufferInfos[i].buffer = vkBuffer.DeviceBuffer;
                    bufferInfos[i].range = vkBuffer.SizeInBytes;
                    descriptorWrites[i].pBufferInfo = &bufferInfos[i];
                }
                else if (type == VkDescriptorType.SampledImage)
                {
                    VkTextureView textureView = Util.AssertSubtype<BindableResource, VkTextureView>(_boundResources[i]);
                    imageInfos[i].imageView = textureView.ImageView;
                    imageInfos[i].imageLayout = VkImageLayout.ShaderReadOnlyOptimal;
                    descriptorWrites[i].pImageInfo = &imageInfos[i];
                }
                else if (type == VkDescriptorType.StorageImage)
                {
                    VkTextureView textureView = Util.AssertSubtype<BindableResource, VkTextureView>(_boundResources[i]);
                    imageInfos[i].imageView = textureView.ImageView;
                    imageInfos[i].imageLayout = VkImageLayout.General;
                    descriptorWrites[i].pImageInfo = &imageInfos[i];
                }
                else if (type == VkDescriptorType.Sampler)
                {
                    VkSampler sampler = Util.AssertSubtype<BindableResource, VkSampler>(_boundResources[i]);
                    imageInfos[i].sampler = sampler.DeviceSampler;
                    descriptorWrites[i].pImageInfo = &imageInfos[i];
                }

                if (_boundResources[i] is VkDeferredDisposal vdd)
                {
                    vdd.ReferenceTracker.Increment();
                }
            }

            vkUpdateDescriptorSets(_gd.Device, descriptorWriteCount, descriptorWrites, 0, null);
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

        public override void Dispose()
        {
            _gd.DeferredDisposal(this);
        }

        public void DestroyResources()
        {
            if (!_disposed)
            {
                _disposed = true;
                _gd.DescriptorPoolManager.Free(_descriptorAllocationToken, _descriptorCounts);
                foreach (var resource in _boundResources)
                {
                    if (resource is VkDeferredDisposal vdd)
                    {
                        vdd.ReferenceTracker.Decrement();
                    }
                }
            }
        }
    }
}