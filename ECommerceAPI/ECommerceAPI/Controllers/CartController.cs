using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using ECommerceAPI.Data;
using ECommerceAPI.Models;

namespace ECommerceAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class CartController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public CartController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetUserId()
        {
            return int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
        }

        // GET: api/cart
        [HttpGet]
        public async Task<ActionResult<CartDto>> GetCart()
        {
            var userId = GetUserId();

            var cart = await _context.Carts
                .Include(c => c.CartItems)
                .ThenInclude(ci => ci.Product)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (cart == null)
            {
                // Create cart if it doesn't exist
                cart = new Cart { UserId = userId };
                _context.Carts.Add(cart);
                await _context.SaveChangesAsync();
            }

            var cartDto = new CartDto
            {
                Id = cart.Id,
                Items = cart.CartItems.Select(ci => new CartItemDto
                {
                    Id = ci.Id,
                    Product = new ProductDto
                    {
                        Id = ci.Product.Id,
                        Name = ci.Product.Name,
                        Description = ci.Product.Description,
                        Price = ci.Product.Price,
                        StockQuantity = ci.Product.StockQuantity,
                        Category = ci.Product.Category,
                        ImageUrl = ci.Product.ImageUrl,
                        IsActive = ci.Product.IsActive
                    },
                    Quantity = ci.Quantity,
                    Subtotal = ci.Product.Price * ci.Quantity
                }).ToList(),
                TotalAmount = cart.CartItems.Sum(ci => ci.Product.Price * ci.Quantity)
            };

            return Ok(cartDto);
        }

        // POST: api/cart/items
        [HttpPost("items")]
        public async Task<ActionResult> AddToCart(AddToCartDto addToCartDto)
        {
            var userId = GetUserId();

            // Check if product exists and has enough stock
            var product = await _context.Products.FindAsync(addToCartDto.ProductId);
            if (product == null || !product.IsActive)
            {
                return NotFound(new { message = "Product not found" });
            }

            if (product.StockQuantity < addToCartDto.Quantity)
            {
                return BadRequest(new { message = "Insufficient stock" });
            }

            // Get or create cart
            var cart = await _context.Carts
                .Include(c => c.CartItems)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (cart == null)
            {
                cart = new Cart { UserId = userId };
                _context.Carts.Add(cart);
                await _context.SaveChangesAsync();
            }

            // Check if item already in cart
            var existingItem = cart.CartItems.FirstOrDefault(ci => ci.ProductId == addToCartDto.ProductId);

            if (existingItem != null)
            {
                // Update quantity
                existingItem.Quantity += addToCartDto.Quantity;

                if (product.StockQuantity < existingItem.Quantity)
                {
                    return BadRequest(new { message = "Insufficient stock" });
                }
            }
            else
            {
                // Add new item
                var cartItem = new CartItem
                {
                    CartId = cart.Id,
                    ProductId = addToCartDto.ProductId,
                    Quantity = addToCartDto.Quantity
                };
                _context.CartItems.Add(cartItem);
            }

            cart.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Item added to cart successfully" });
        }

        // PUT: api/cart/items/5
        [HttpPut("items/{cartItemId}")]
        public async Task<ActionResult> UpdateCartItem(int cartItemId, UpdateCartItemDto updateCartItemDto)
        {
            var userId = GetUserId();

            var cartItem = await _context.CartItems
                .Include(ci => ci.Cart)
                .Include(ci => ci.Product)
                .FirstOrDefaultAsync(ci => ci.Id == cartItemId && ci.Cart.UserId == userId);

            if (cartItem == null)
            {
                return NotFound(new { message = "Cart item not found" });
            }

            // Check stock
            if (cartItem.Product.StockQuantity < updateCartItemDto.Quantity)
            {
                return BadRequest(new { message = "Insufficient stock" });
            }

            cartItem.Quantity = updateCartItemDto.Quantity;
            cartItem.Cart.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Cart item updated successfully" });
        }

        // DELETE: api/cart/items/5
        [HttpDelete("items/{cartItemId}")]
        public async Task<ActionResult> RemoveFromCart(int cartItemId)
        {
            var userId = GetUserId();

            var cartItem = await _context.CartItems
                .Include(ci => ci.Cart)
                .FirstOrDefaultAsync(ci => ci.Id == cartItemId && ci.Cart.UserId == userId);

            if (cartItem == null)
            {
                return NotFound(new { message = "Cart item not found" });
            }

            _context.CartItems.Remove(cartItem);
            cartItem.Cart.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Item removed from cart successfully" });
        }

        // DELETE: api/cart
        [HttpDelete]
        public async Task<ActionResult> ClearCart()
        {
            var userId = GetUserId();

            var cart = await _context.Carts
                .Include(c => c.CartItems)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (cart == null)
            {
                return NotFound(new { message = "Cart not found" });
            }

            _context.CartItems.RemoveRange(cart.CartItems);
            cart.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Cart cleared successfully" });
        }
    }
}