using System.Collections.Generic;
using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
using API.Errors;
using API.Tests.Helpers;                 // ControllerTestHelpers (Identity manager mocks, claims principals, async Users)
using AutoMapper;
using Core.Entities.Identity;            // AppUser, Address (Identity.Address — NOT OrderAggregate)
using Core.Interfaces;                   // ITokenService
using FluentAssertions;
using Microsoft.AspNetCore.Identity;     // UserManager, SignInManager, IdentityResult, IdentityError
                                         // (SignInResult is referenced fully-qualified inline to avoid the
                                         //  Microsoft.AspNetCore.Mvc.SignInResult ambiguity — see below)
using Microsoft.AspNetCore.Mvc;          // ActionResult<T> and the *ObjectResult result types
using Moq;
using Xunit;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="AccountController"/> — the most collaborator-heavy controller in the
    /// API presentation layer. The controller depends on <see cref="UserManager{AppUser}"/>,
    /// <see cref="SignInManager{AppUser}"/>, <see cref="ITokenService"/> and
    /// <see cref="IMapper"/>; every collaborator is replaced with a Moq test double so each action
    /// is exercised in complete isolation (no database, no HTTP pipeline, no external service).
    /// <para>
    /// The Identity manager mocks and authenticated <see cref="System.Security.Claims.ClaimsPrincipal"/>
    /// values are sourced from the sibling helper <see cref="ControllerTestHelpers"/> (AAP §0.4.4), and
    /// the auth-aware actions (<c>GetCurrentUser</c>/<c>GetUserAddress</c>/<c>UpdateUserAddress</c>) rely
    /// on <see cref="ControllerTestHelpers.MockUserManagerUsers"/> to expose an async-queryable
    /// <c>UserManager.Users</c> so the static <c>UserManagerExtensions</c> methods resolve.
    /// </para>
    /// <para>
    /// Tests follow the <c>MethodName_StateUnderTest_ExpectedBehavior</c> convention with an explicit
    /// Arrange-Act-Assert layout and FluentAssertions. Fresh mocks are constructed for every test via
    /// <see cref="CreateController"/>, guaranteeing no shared mutable state between tests.
    /// </para>
    /// </summary>
    public class AccountControllerTests
    {
        // ------------------------------------------------------------------
        // Arrange helper — fresh mocks + controller per test (no shared state)
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds a fully-mocked <see cref="AccountController"/> and surfaces the underlying mocks so a
        /// test can configure only the collaborators it cares about. A brand-new set of mocks is created
        /// on every invocation, satisfying the "fresh mocks per test" requirement.
        /// </summary>
        /// <param name="userManager">The mocked <see cref="UserManager{AppUser}"/>.</param>
        /// <param name="signInManager">The mocked <see cref="SignInManager{AppUser}"/> (wrapping <paramref name="userManager"/>).</param>
        /// <param name="tokenService">The mocked <see cref="ITokenService"/>.</param>
        /// <param name="mapper">The mocked AutoMapper <see cref="IMapper"/>.</param>
        /// <returns>A ready-to-exercise <see cref="AccountController"/>.</returns>
        private static AccountController CreateController(
            out Mock<UserManager<AppUser>> userManager,
            out Mock<SignInManager<AppUser>> signInManager,
            out Mock<ITokenService> tokenService,
            out Mock<IMapper> mapper)
        {
            userManager = ControllerTestHelpers.GetMockUserManager();
            signInManager = ControllerTestHelpers.GetMockSignInManager(userManager);
            tokenService = new Mock<ITokenService>();
            mapper = new Mock<IMapper>();

            return new AccountController(
                userManager.Object, signInManager.Object, tokenService.Object, mapper.Object);
        }

        // ==================================================================
        // CheckEmailExistsAsync — GET api/account/emailexists (direct bool)
        // ==================================================================

        [Fact]
        public async Task CheckEmailExistsAsync_WhenEmailInUse_ReturnsTrue()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            userManager.Setup(m => m.FindByEmailAsync("used@test.com"))
                .ReturnsAsync(new AppUser());

            // Act
            var result = await controller.CheckEmailExistsAsync("used@test.com");

            // Assert
            result.Value.Should().BeTrue();
        }

        [Fact]
        public async Task CheckEmailExistsAsync_WhenEmailNotInUse_ReturnsFalse()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            userManager.Setup(m => m.FindByEmailAsync("free@test.com"))
                .ReturnsAsync((AppUser)null);

            // Act
            var result = await controller.CheckEmailExistsAsync("free@test.com");

            // Assert
            result.Value.Should().BeFalse();
        }

        // ==================================================================
        // Login — POST api/account/login (Unauthorized wrapper / direct UserDto)
        // ==================================================================

        [Fact]
        public async Task Login_WhenUserNotFound_ReturnsUnauthorized()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            var loginDto = new LoginDto { Email = "missing@test.com", Password = "Pa$$w0rd" };
            userManager.Setup(m => m.FindByEmailAsync(loginDto.Email))
                .ReturnsAsync((AppUser)null);

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            result.Result.Should().BeOfType<UnauthorizedObjectResult>();
            var unauthorized = (UnauthorizedObjectResult)result.Result;
            unauthorized.Value.Should().BeOfType<ApiResponse>();
            ((ApiResponse)unauthorized.Value).StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task Login_WhenPasswordInvalid_ReturnsUnauthorized()
        {
            // Arrange
            var controller = CreateController(out var userManager, out var signInManager, out _, out _);
            var loginDto = new LoginDto { Email = "bob@test.com", Password = "wrong" };
            userManager.Setup(m => m.FindByEmailAsync(loginDto.Email))
                .ReturnsAsync(new AppUser { Email = loginDto.Email });
            signInManager.Setup(s => s.CheckPasswordSignInAsync(It.IsAny<AppUser>(), It.IsAny<string>(), false))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Failed);

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            result.Result.Should().BeOfType<UnauthorizedObjectResult>();
            var unauthorized = (UnauthorizedObjectResult)result.Result;
            unauthorized.Value.Should().BeOfType<ApiResponse>();
            ((ApiResponse)unauthorized.Value).StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task Login_WithValidCredentials_ReturnsUserDtoWithToken()
        {
            // Arrange
            var controller = CreateController(out var userManager, out var signInManager, out var tokenService, out _);
            var loginDto = new LoginDto { Email = "bob@test.com", Password = "Pa$$w0rd" };
            userManager.Setup(m => m.FindByEmailAsync(loginDto.Email))
                .ReturnsAsync(new AppUser { Email = "bob@test.com", DisplayName = "Bob" });
            signInManager.Setup(s => s.CheckPasswordSignInAsync(It.IsAny<AppUser>(), It.IsAny<string>(), false))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);
            tokenService.Setup(t => t.CreateToken(It.IsAny<AppUser>())).Returns("test-token");

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            result.Value.Should().NotBeNull();
            result.Value.Email.Should().Be("bob@test.com");
            result.Value.DisplayName.Should().Be("Bob");
            result.Value.Token.Should().Be("test-token");
        }

        // ==================================================================
        // Register — POST api/account/register (validation / bad-request / direct UserDto)
        // ==================================================================

        [Fact]
        public async Task Register_WhenEmailAlreadyInUse_ReturnsBadRequestValidationError()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            var dto = new RegisterDto { Email = "used@test.com", DisplayName = "X", Password = "Pa$$w0rd" };
            // Register calls its own CheckEmailExistsAsync(...).Result.Value, driven purely by FindByEmailAsync.
            userManager.Setup(m => m.FindByEmailAsync(dto.Email))
                .ReturnsAsync(new AppUser());

            // Act
            var result = await controller.Register(dto);

            // Assert
            result.Result.Should().BeOfType<BadRequestObjectResult>();
            var badRequest = (BadRequestObjectResult)result.Result;
            badRequest.Value.Should().BeOfType<ApiValidationErrorResponose>();
            var validationError = (ApiValidationErrorResponose)badRequest.Value;
            validationError.StatusCode.Should().Be(400);
            validationError.Errors.Should().Contain("Email address already in use");
        }

        [Fact]
        public async Task Register_WhenCreateFails_ReturnsBadRequestApiResponse400()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            var dto = new RegisterDto { Email = "new@test.com", DisplayName = "New", Password = "Pa$$w0rd" };
            userManager.Setup(m => m.FindByEmailAsync(dto.Email))
                .ReturnsAsync((AppUser)null);
            userManager.Setup(m => m.CreateAsync(It.IsAny<AppUser>(), dto.Password))
                .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "WeakPassword", Description = "weak" }));

            // Act
            var result = await controller.Register(dto);

            // Assert
            result.Result.Should().BeOfType<BadRequestObjectResult>();
            var badRequest = (BadRequestObjectResult)result.Result;
            badRequest.Value.Should().BeOfType<ApiResponse>();
            ((ApiResponse)badRequest.Value).StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task Register_WithValidData_ReturnsUserDtoWithToken()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out var tokenService, out _);
            var dto = new RegisterDto { Email = "new@test.com", DisplayName = "New", Password = "Pa$$w0rd" };
            userManager.Setup(m => m.FindByEmailAsync(dto.Email))
                .ReturnsAsync((AppUser)null);
            userManager.Setup(m => m.CreateAsync(It.IsAny<AppUser>(), dto.Password))
                .ReturnsAsync(IdentityResult.Success);
            tokenService.Setup(t => t.CreateToken(It.IsAny<AppUser>())).Returns("test-token");

            // Act
            var result = await controller.Register(dto);

            // Assert
            result.Value.Should().NotBeNull();
            result.Value.Email.Should().Be(dto.Email);
            result.Value.DisplayName.Should().Be(dto.DisplayName);
            result.Value.Token.Should().Be("test-token");
        }

        // ==================================================================
        // GetCurrentUser — [Authorize] GET api/account (async Users via static extension)
        // ==================================================================

        [Fact]
        public async Task GetCurrentUser_WithAuthenticatedUser_ReturnsUserDto()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out var tokenService, out _);
            var user = new AppUser { Email = "bob@test.com", DisplayName = "Bob" };
            // FindByEmailFromClaimsPrinciple runs Users.SingleOrDefaultAsync(x => x.Email == email);
            // wire an async-queryable Users so the static extension resolves against the in-memory list.
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { user });
            tokenService.Setup(t => t.CreateToken(It.IsAny<AppUser>())).Returns("test-token");
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.GetCurrentUser();

            // Assert
            result.Value.Should().NotBeNull();
            result.Value.Email.Should().Be("bob@test.com");
            result.Value.DisplayName.Should().Be("Bob");
            result.Value.Token.Should().Be("test-token");
        }

        // ==================================================================
        // GetUserAddress — [Authorize] GET api/account/address (async Users; Include is a no-op here)
        // ==================================================================

        [Fact]
        public async Task GetUserAddress_WithAuthenticatedUser_ReturnsMappedAddressDto()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out var mapper);
            var address = new Address { FirstName = "Bob" };
            var user = new AppUser { Email = "bob@test.com", Address = address };
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { user });
            var dto = new AddressDto();
            mapper.Setup(m => m.Map<Address, AddressDto>(address)).Returns(dto);
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.GetUserAddress();

            // Assert
            result.Value.Should().BeSameAs(dto);
        }

        // ==================================================================
        // UpdateUserAddress — [Authorize] PUT api/account/address (Ok / BadRequest string)
        // ==================================================================

        [Fact]
        public async Task UpdateUserAddress_WhenUpdateSucceeds_ReturnsOkWithAddressDto()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out var mapper);
            var user = new AppUser { Email = "bob@test.com" };
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { user });
            var inputDto = new AddressDto();
            var mappedAddress = new Address();
            mapper.Setup(m => m.Map<AddressDto, Address>(inputDto)).Returns(mappedAddress);
            var returnDto = new AddressDto();
            mapper.Setup(m => m.Map<Address, AddressDto>(It.IsAny<Address>())).Returns(returnDto);
            userManager.Setup(m => m.UpdateAsync(It.IsAny<AppUser>())).ReturnsAsync(IdentityResult.Success);
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.UpdateUserAddress(inputDto);

            // Assert
            result.Result.Should().BeOfType<OkObjectResult>();
            var ok = (OkObjectResult)result.Result;
            ok.Value.Should().BeSameAs(returnDto);
        }

        [Fact]
        public async Task UpdateUserAddress_WhenUpdateFails_ReturnsBadRequestWithMessage()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out var mapper);
            var user = new AppUser { Email = "bob@test.com" };
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { user });
            var inputDto = new AddressDto();
            var mappedAddress = new Address();
            mapper.Setup(m => m.Map<AddressDto, Address>(inputDto)).Returns(mappedAddress);
            mapper.Setup(m => m.Map<Address, AddressDto>(It.IsAny<Address>())).Returns(new AddressDto());
            userManager.Setup(m => m.UpdateAsync(It.IsAny<AppUser>()))
                .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "UpdateFailed", Description = "nope" }));
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.UpdateUserAddress(inputDto);

            // Assert
            result.Result.Should().BeOfType<BadRequestObjectResult>();
            var badRequest = (BadRequestObjectResult)result.Result;
            // NOTE: exact production string, "then" typo included — do NOT "correct" it.
            badRequest.Value.Should().Be("Problem updating then user");
        }
    }
}
