using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using ECommerceAPI.Data;
using ECommerceAPI.Models;
using Stripe;

namespace ECommerceAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class OrdersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        public OrdersController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        private int GetUserId()
        {
            return int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
        }

        // POST: api/orders/checkout
        [HttpPost("checkout")]
        public async Task<ActionResult<OrderDto>> Checkout(CheckoutDto checkoutDto)
        {
            var userId = GetUserId();

            // Get cart with items
            var cart = await _context.Carts
                .Include(c => c.CartItems)
                .ThenInclude(ci => ci.Product)
                .FirstOrDefaultAsync(c => c.UserId == userId);

            if (cart == null || !cart.CartItems.Any())
            {
                return BadRequest(new { message = "Cart is empty" });
            }

            // Validate stock availability
            foreach (var item in cart.CartItems)
            {
                if (item.Product.StockQuantity < item.Quantity)
                {
                    return BadRequest(new { message = $"Insufficient stock for {item.Product.Name}" });
                }
            }

            // Calculate total
            var totalAmount = cart.CartItems.Sum(ci => ci.Product.Price * ci.Quantity);

            // Process payment with Stripe
            string paymentIntentId = null;
            try
            {
                StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];

                var paymentIntentService = new PaymentIntentService();
                var paymentIntentOptions = new PaymentIntentCreateOptions
                {
                    Amount = (long)(totalAmount * 100), // Convert to cents
                    Currency = "usd",
                    PaymentMethod = checkoutDto.PaymentMethodId,
                    Confirm = true,
                    AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                    {
                        Enabled = true,
                        AllowRedirects = "never"
                    }
                };

                var paymentIntent = await paymentIntentService.CreateAsync(paymentIntentOptions);
                paymentIntentId = paymentIntent.Id;

                if (paymentIntent.Status != "succeeded")
                {
                    return BadRequest(new { message = "Payment failed" });
                }
            }
            catch (StripeException ex)
            {
                return BadRequest(new { message = $"Payment error: {ex.Message}" });
            }

            // Create order
            var order = new Models.Order
            {
                UserId = userId,
                TotalAmount = totalAmount,
                Status = OrderStatus.Processing,
                PaymentIntentId = paymentIntentId,
                ShippingAddress = checkoutDto.ShippingAddress
            };

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            // Create order items and update stock
            foreach (var cartItem in cart.CartItems)
            {
                var orderItem = new OrderItem
                {
                    OrderId = order.Id,
                    ProductId = cartItem.ProductId,
                    Quantity = cartItem.Quantity,
                    PriceAtPurchase = cartItem.Product.Price
                };

                _context.OrderItems.Add(orderItem);

                // Update product stock
                cartItem.Product.StockQuantity -= cartItem.Quantity;
            }

            // Clear cart
            _context.CartItems.RemoveRange(cart.CartItems);

            await _context.SaveChangesAsync();

            // Load order with items for response
            var createdOrder = await _context.Orders
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .FirstOrDefaultAsync(o => o.Id == order.Id);

            var orderDto = MapOrderToDto(createdOrder);

            return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, orderDto);
        }

        // GET: api/orders
        [HttpGet]
        public async Task<ActionResult<IEnumerable<OrderDto>>> GetOrders()
        {
            var userId = GetUserId();

            var orders = await _context.Orders
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            var orderDtos = orders.Select(o => MapOrderToDto(o)).ToList();

            return Ok(orderDtos);
        }

        // GET: api/orders/5
        [HttpGet("{id}")]
        public async Task<ActionResult<OrderDto>> GetOrder(int id)
        {
            var userId = GetUserId();

            var order = await _context.Orders
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId);

            if (order == null)
            {
                return NotFound(new { message = "Order not found" });
            }

            var orderDto = MapOrderToDto(order);

            return Ok(orderDto);
        }

        // GET: api/orders/all (Admin only)
        [HttpGet("all")]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult<IEnumerable<OrderDto>>> GetAllOrders(
            [FromQuery] OrderStatus? status = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            var query = _context.Orders
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .Include(o => o.User)
                .AsQueryable();

            if (status.HasValue)
            {
                query = query.Where(o => o.Status == status.Value);
            }

            var totalCount = await query.CountAsync();
            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            Response.Headers.Add("X-Total-Count", totalCount.ToString());

            var orderDtos = orders.Select(o => MapOrderToDto(o)).ToList();

            return Ok(orderDtos);
        }

        // PUT: api/orders/5/status (Admin only)
        [HttpPut("{id}/status")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateOrderStatus(int id, [FromBody] OrderStatus status)
        {
            var order = await _context.Orders.FindAsync(id);

            if (order == null)
            {
                return NotFound(new { message = "Order not found" });
            }

            order.Status = status;
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Order status updated successfully" });
        }

        // DELETE: api/orders/5 (Cancel order)
        [HttpDelete("{id}")]
        public async Task<IActionResult> CancelOrder(int id)
        {
            var userId = GetUserId();

            var order = await _context.Orders
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId);

            if (order == null)
            {
                return NotFound(new { message = "Order not found" });
            }

            if (order.Status != OrderStatus.Pending && order.Status != OrderStatus.Processing)
            {
                return BadRequest(new { message = "Cannot cancel order in current status" });
            }

            // Restore stock
            foreach (var item in order.OrderItems)
            {
                item.Product.StockQuantity += item.Quantity;
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Order cancelled successfully" });
        }

        private OrderDto MapOrderToDto(Models.Order order)
        {
            return new OrderDto
            {
                Id = order.Id,
                TotalAmount = order.TotalAmount,
                Status = order.Status,
                ShippingAddress = order.ShippingAddress,
                CreatedAt = order.CreatedAt,
                Items = order.OrderItems.Select(oi => new OrderItemDto
                {
                    Id = oi.Id,
                    Product = new ProductDto
                    {
                        Id = oi.Product.Id,
                        Name = oi.Product.Name,
                        Description = oi.Product.Description,
                        Price = oi.Product.Price,
                        StockQuantity = oi.Product.StockQuantity,
                        Category = oi.Product.Category,
                        ImageUrl = oi.Product.ImageUrl,
                        IsActive = oi.Product.IsActive
                    },
                    Quantity = oi.Quantity,
                    PriceAtPurchase = oi.PriceAtPurchase,
                    Subtotal = oi.PriceAtPurchase * oi.Quantity
                }).ToList()
            };
        }
    }
}