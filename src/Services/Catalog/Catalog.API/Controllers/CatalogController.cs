﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.eShopOnContainers.BuildingBlocks.EventBus.Abstractions;
using Microsoft.eShopOnContainers.BuildingBlocks.IntegrationEventLogEF.Services;
using Microsoft.eShopOnContainers.Services.Catalog.API.Infrastructure;
using Microsoft.eShopOnContainers.Services.Catalog.API.IntegrationEvents.Events;
using Microsoft.eShopOnContainers.Services.Catalog.API.Model;
using Microsoft.eShopOnContainers.Services.Catalog.API.ViewModel;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.eShopOnContainers.BuildingBlocks.EventBus.Events;
using Microsoft.eShopOnContainers.BuildingBlocks.IntegrationEventLogEF.Utilities;
using Catalog.API.IntegrationEvents;

namespace Microsoft.eShopOnContainers.Services.Catalog.API.Controllers
{
    [Route("api/v1/[controller]")]
    public class CatalogController : ControllerBase
    {
        private readonly CatalogContext _catalogContext;
        private readonly IOptionsSnapshot<Settings> _settings;
        private readonly ICatalogIntegrationEventService _catalogIntegrationEventService;

        public CatalogController(CatalogContext Context, IOptionsSnapshot<Settings> settings, ICatalogIntegrationEventService catalogIntegrationEventService)
        {
            _catalogContext = Context;
            _catalogIntegrationEventService = catalogIntegrationEventService;
            _settings = settings;

            ((DbContext)Context).ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        }

        // GET api/v1/[controller]/items[?pageSize=3&pageIndex=10]
        [HttpGet]
        [Route("[action]")]
        public async Task<IActionResult> Items([FromQuery]int pageSize = 10, [FromQuery]int pageIndex = 0)

        {
            var totalItems = await _catalogContext.CatalogItems
                .LongCountAsync();

            var itemsOnPage = await _catalogContext.CatalogItems
                .OrderBy(c=>c.Name)
                .Skip(pageSize * pageIndex)
                .Take(pageSize)
                .ToListAsync();

            itemsOnPage = ComposePicUri(itemsOnPage);

            var model = new PaginatedItemsViewModel<CatalogItem>(
                pageIndex, pageSize, totalItems, itemsOnPage);           

            return Ok(model);
        }

        // GET api/v1/[controller]/items/withname/samplename[?pageSize=3&pageIndex=10]
        [HttpGet]
        [Route("[action]/withname/{name:minlength(1)}")]
        public async Task<IActionResult> Items(string name, [FromQuery]int pageSize = 10, [FromQuery]int pageIndex = 0)
        {

            var totalItems = await _catalogContext.CatalogItems
                .Where(c => c.Name.StartsWith(name))
                .LongCountAsync();

            var itemsOnPage = await _catalogContext.CatalogItems
                .Where(c => c.Name.StartsWith(name))
                .Skip(pageSize * pageIndex)
                .Take(pageSize)
                .ToListAsync();

            itemsOnPage = ComposePicUri(itemsOnPage);

            var model = new PaginatedItemsViewModel<CatalogItem>(
                pageIndex, pageSize, totalItems, itemsOnPage);

            return Ok(model);
        }

        // GET api/v1/[controller]/items/type/1/brand/null[?pageSize=3&pageIndex=10]
        [HttpGet]
        [Route("[action]/type/{catalogTypeId}/brand/{catalogBrandId}")]
        public async Task<IActionResult> Items(int? catalogTypeId, int? catalogBrandId, [FromQuery]int pageSize = 10, [FromQuery]int pageIndex = 0)
        {
            var root = (IQueryable<CatalogItem>)_catalogContext.CatalogItems;

            if (catalogTypeId.HasValue)
            {
                root = root.Where(ci => ci.CatalogTypeId == catalogTypeId);
            }

            if (catalogBrandId.HasValue)
            {
                root = root.Where(ci => ci.CatalogBrandId == catalogBrandId);
            }

            var totalItems = await root
                .LongCountAsync();

            var itemsOnPage = await root
                .Skip(pageSize * pageIndex)
                .Take(pageSize)
                .ToListAsync();

            itemsOnPage = ComposePicUri(itemsOnPage);

            var model = new PaginatedItemsViewModel<CatalogItem>(
                pageIndex, pageSize, totalItems, itemsOnPage);

            return Ok(model);
        }

        // GET api/v1/[controller]/CatalogTypes
        [HttpGet]
        [Route("[action]")]
        public async Task<IActionResult> CatalogTypes()
        {
            var items = await _catalogContext.CatalogTypes
                .ToListAsync();

            return Ok(items);
        }

        // GET api/v1/[controller]/CatalogBrands
        [HttpGet]
        [Route("[action]")]
        public async Task<IActionResult> CatalogBrands()
        {
            var items = await _catalogContext.CatalogBrands
                .ToListAsync();

            return Ok(items);
        }

        //POST api/v1/[controller]/update
        [Route("update")]
        [HttpPost]
        public async Task<IActionResult> UpdateProduct([FromBody]CatalogItem productToUpdate)
        {
            var catalogItem = await _catalogContext.CatalogItems.SingleOrDefaultAsync(i => i.Id == productToUpdate.Id);
            if (catalogItem == null) return NotFound();
            var raiseProductPriceChangedEvent = catalogItem.Price != productToUpdate.Price;
            var oldPrice = catalogItem.Price;
            
            // Update current product
            catalogItem = productToUpdate;
            _catalogContext.CatalogItems.Update(catalogItem);

            if (raiseProductPriceChangedEvent) // Save and publish event if price has changed
            {                               
                var priceChangedEvent = new ProductPriceChangedIntegrationEvent(catalogItem.Id, productToUpdate.Price, oldPrice);
                // Achieving atomicity between original Catalog database operation and the IntegrationEventLog thanks to a local transaction
                await _catalogIntegrationEventService.SaveEventAsync(priceChangedEvent);
                // Publish to Event Bus only if product price changed
                await _catalogIntegrationEventService.PublishAsync(priceChangedEvent);
            }
            else // Save updated product
            {
                await _catalogContext.SaveChangesAsync();
            }           

            return Ok();
        }

        //POST api/v1/[controller]/create
        [Route("create")]
        [HttpPost]
        public async Task<IActionResult> CreateProduct([FromBody]CatalogItem product)
        {
            _catalogContext.CatalogItems.Add(
                new CatalogItem
                {
                    CatalogBrandId = product.CatalogBrandId,
                    CatalogTypeId = product.CatalogTypeId,
                    Description = product.Description,
                    Name = product.Name,
                    PictureUri = product.PictureUri,
                    Price = product.Price
                });

            await _catalogContext.SaveChangesAsync();

            return Ok();
        }

        //DELETE api/v1/[controller]/id
        [Route("{id}")]
        [HttpDelete]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            var product = _catalogContext.CatalogItems.SingleOrDefault(x => x.Id == id);

            if (product == null)
            {
                return NotFound();
            }            

            _catalogContext.CatalogItems.Remove(product);
            await _catalogContext.SaveChangesAsync();

            return Ok();
        }

        private List<CatalogItem> ComposePicUri(List<CatalogItem> items) {
            var baseUri = _settings.Value.ExternalCatalogBaseUrl;
            items.ForEach(x =>
            {
                x.PictureUri = x.PictureUri.Replace("http://externalcatalogbaseurltobereplaced", baseUri);
            });

            return items;
        }
    }
}
