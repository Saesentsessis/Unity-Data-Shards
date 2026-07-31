using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Storage.Transforms
{
	/// <summary>
	/// AES-256-CBC encryption with an HMAC-SHA256 authentication tag, in encrypt-then-MAC order.
	/// Wire format is <c>[IV:16][ciphertext][HMAC:32]</c>; the tag covers <c>IV || ciphertext</c> and
	/// is verified in constant time <b>before</b> anything is decrypted.
	/// <para>
	/// <b>On a shipped game this is obfuscation, not secrecy.</b> The key travels inside the build, so
	/// a determined player can extract it. What the tag does buy is real tamper <i>detection</i>: the
	/// save envelope's xxHash3 checksum is unkeyed, so anyone editing a save can simply recompute it,
	/// whereas forging this HMAC requires the key. Treat it as raising the cost of casual editing, and
	/// never as protection for data that must stay secret from the player.
	/// </para>
	/// <para>
	/// AES-GCM would be the better primitive but is unusable here: it throws
	/// <c>PlatformNotSupportedException</c> at runtime on iOS, tvOS and WebGL, while compiling
	/// perfectly on every platform.
	/// </para>
	/// <para>
	/// <b>Performance.</b> Nothing proportional to the payload is allocated on the GC heap. The cipher
	/// streams straight into a pooled scratch arena, the tag is computed span-to-span through
	/// <see cref="HashAlgorithm.TryComputeHash"/>, and the result reaches the caller's writer in a
	/// single copy. The <see cref="Aes"/>, <see cref="HMACSHA256"/> and scratch instances are created
	/// once and reused, which is why this type is <see cref="IDisposable"/>.
	/// </para>
	/// <para>
	/// <b>Ownership.</b> An instance belongs to exactly one <see cref="TransformStorage"/>, which
	/// disposes it — wiping the derived key and returning the scratch arena zeroed. Do not hand the
	/// same instance to a second chain: the IV buffer and scratch arena below are per-operation
	/// state, so two storages driving one instance would interleave through them.
	/// </para>
	/// <para>
	/// <b>IL2CPP:</b> AES is reached by reflection and gets stripped unless preserved. Add to
	/// <c>Assets/link.xml</c>:
	/// <code>
	/// &lt;linker&gt;
	///   &lt;assembly fullname="System.Core"&gt;
	///     &lt;type fullname="System.Security.Cryptography.AesManaged" preserve="all" /&gt;
	///   &lt;/assembly&gt;
	/// &lt;/linker&gt;
	/// </code>
	/// </para>
	/// </summary>
	public sealed class AesCbcHmacTransform : IStorageTransform, IDisposable
	{
		private const int MinimalIterationCount = 16_384;
		private const int IvSize = 16;
		private const int TagSize = 32;
		private const int KeySize = 32;
		private const int MinimumKeyLength = 16;
		
		internal const Options DefaultOptions = Options.ClearPooledArrays;

		// Distinct labels so the same master key never serves as both cipher and MAC key.
		private static readonly byte[] EncryptionLabel = Encoding.ASCII.GetBytes("uds:enc:v1");
		private static readonly byte[] AuthenticationLabel = Encoding.ASCII.GetBytes("uds:mac:v1");

		private readonly Aes _aes;
		private readonly HMACSHA256 _hmac;
		private readonly Options _options;

		// Held rather than re-read from Aes.Key on every call: that property's getter returns a fresh
		// copy of the key each time it is touched, so using it per operation would scatter live key
		// material across the GC heap and allocate on the hot path for nothing.
		private readonly byte[] _encryptionKey;

		// Likewise reused: CreateEncryptor/CreateDecryptor copy the IV internally, so one instance
		// buffer serves every call and no 16-byte array is allocated per save.
		private readonly byte[] _iv = new byte[IvSize];

		// Reused across calls: TransformStorage runs one operation at a time per instance, and the
		// IStorageTransform contract requires no state to carry *between* calls — a scratch buffer
		// carries none.
		private readonly PooledArrayBufferWriter _scratch;

		/// <param name="masterKey">
		/// Key material of at least 16 bytes. Two independent 32-byte subkeys are derived from it, so
		/// the caller never has to manage a cipher key and a MAC key separately.
		/// </param>
		/// <param name="options">
		/// Flags that define transform's behavior.
		/// </param>
		public AesCbcHmacTransform(ReadOnlySpan<byte> masterKey, Options options = DefaultOptions)
		{
			if (masterKey.Length < MinimumKeyLength)
				throw new ArgumentException(
					$"Key must be at least {MinimumKeyLength} bytes; got {masterKey.Length}.", nameof(masterKey));

			_encryptionKey = DeriveSubkey(masterKey, EncryptionLabel);

			_aes = Aes.Create();
			_aes.KeySize = KeySize * 8;
			_aes.Mode = CipherMode.CBC;
			_aes.Padding = PaddingMode.PKCS7;
			_aes.Key = _encryptionKey;

			// HMACSHA256 copies the key into its own state, so the derived array is wiped immediately
			// rather than kept in a field — unlike the cipher key, nothing needs it again.
			var authenticationKey = DeriveSubkey(masterKey, AuthenticationLabel);
			_hmac = new HMACSHA256(authenticationKey);
			Array.Clear(authenticationKey, 0, authenticationKey.Length);

			_options = options;
			_scratch = new PooledArrayBufferWriter(clearOnRelease: (options & Options.ClearPooledArrays) != 0);
		}

		/// <summary>Convenience overload: derives the master key from a passphrase via PBKDF2.</summary>
		/// <param name="passphrase">Secret text. Still ships inside the build — see the type remarks.</param>
		/// <param name="salt">Application-specific salt; must be identical on save and load.</param>
		/// <param name="iterations">PBKDF2 work factor.</param>
		/// <param name="options">Transform's behaviour flags.</param>
		public AesCbcHmacTransform(string passphrase, byte[] salt, int iterations = 100_000, Options options = DefaultOptions)
			: this(DerivePassphraseKey(passphrase, salt, iterations), options) { }

		/// <summary>
		/// Private step in the passphrase path. A <c>: this(...)</c> chain runs the target constructor
		/// first and this body second, which is the only place the PBKDF2 output can be wiped — the
		/// span overload never learns it was handed a heap array it is allowed to destroy.
		/// </summary>
		private AesCbcHmacTransform(byte[] derivedKey, Options options)
			: this((ReadOnlySpan<byte>)derivedKey, options)
		{
			Array.Clear(derivedKey, 0, derivedKey.Length);
		}

		public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			_scratch.Clear();

			// A fresh IV per call is what makes the same plaintext encrypt differently every save.
			RandomNumberGenerator.Fill(_iv);

			// IV first, so the scratch buffer ends up holding exactly the MAC'd region: IV || ciphertext.
			_iv.CopyTo(_scratch.GetSpan(IvSize));
			_scratch.Advance(IvSize);

			using (var encryptor = _aes.CreateEncryptor(_encryptionKey, _iv))
			using (var stream = new CryptoStream(new BufferWriterStream(_scratch), encryptor, CryptoStreamMode.Write))
			{
				// The ciphertext lands directly in the arena — no intermediate ciphertext array.
				stream.Write(src);
				stream.FlushFinalBlock();
			}

			var authenticated = _scratch.WrittenSpan;
			var output = dst.GetSpan(authenticated.Length + TagSize);

			authenticated.CopyTo(output);

			if (_hmac.TryComputeHash(authenticated, output[authenticated.Length..], out _) == false)
				throw new InvalidOperationException("Failed to compute the authentication tag.");

			dst.Advance(authenticated.Length + TagSize);
		}

		public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			if (src.Length < IvSize + TagSize)
				throw new SaveCorruptedException(
					$"Encrypted payload of {src.Length} bytes is shorter than its {IvSize}-byte IV plus {TagSize}-byte tag.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var bodyLength = src.Length - TagSize;
			var authenticated = src[..bodyLength];
			var storedTag = src[bodyLength..];

			// Authenticate before decrypting: never feed unverified bytes to the cipher.
			Span<byte> computedTag = stackalloc byte[TagSize];

			if (_hmac.TryComputeHash(authenticated, computedTag, out _) == false)
				throw new InvalidOperationException("Failed to compute the authentication tag.");

			if (CryptographicOperations.FixedTimeEquals(computedTag, storedTag) == false)
				throw new SaveCorruptedException(
					"Authentication tag mismatch: the save was modified, or was written with a different key.",
					SaveCorruptedExceptionReason.ChecksumMismatch);

			_scratch.Clear();

			authenticated[..IvSize].CopyTo(_iv);

			try
			{
				using var decryptor = _aes.CreateDecryptor(_encryptionKey, _iv);
				using var stream = new CryptoStream(new BufferWriterStream(_scratch), decryptor, CryptoStreamMode.Write);

				stream.Write(authenticated[IvSize..]);
				stream.FlushFinalBlock();
			}
			catch (CryptographicException exception)
			{
				// The tag already passed, so a padding failure here means a genuine format problem.
				throw new SaveCorruptedException($"Failed to decrypt the save: {exception.Message}");
			}

			var plainText = _scratch.WrittenSpan;
			plainText.CopyTo(dst.GetSpan(plainText.Length));
			dst.Advance(plainText.Length);
		}

		public void Dispose()
		{
			_aes?.Dispose();
			_hmac?.Dispose();

			// Nothing here is reachable afterwards, but the arrays outlive the object until the GC
			// gets to them, so the key and the last IV are zeroed rather than merely dropped.
			if (_encryptionKey != null)
				Array.Clear(_encryptionKey, 0, _encryptionKey.Length);

			Array.Clear(_iv, 0, _iv.Length);

			// The scratch arena wipes itself on the way back to the pool when ClearPooledArrays is
			// set — including on growth, which is where plaintext would otherwise escape long before
			// anything is disposed.
			_scratch?.Dispose();
		}

		/// <summary>HMAC-based subkey derivation: one hash per label, both 32 bytes wide.</summary>
		private static byte[] DeriveSubkey(ReadOnlySpan<byte> masterKey, byte[] label)
		{
			var masterKeyArray = masterKey.ToArray();
			using var hmac = new HMACSHA256(masterKeyArray);
			try
			{
				return hmac.ComputeHash(label);
			}
			finally
			{
				Array.Clear(masterKeyArray, 0, masterKeyArray.Length);
			}
		}

		private static byte[] DerivePassphraseKey(string passphrase, byte[] salt, int iterations)
		{
			if (string.IsNullOrEmpty(passphrase))
				throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));

			if (salt == null || salt.Length < 8)
				throw new ArgumentException("Salt must be at least 8 bytes.", nameof(salt));
			
			if (iterations < MinimalIterationCount)
				throw new ArgumentOutOfRangeException(nameof(iterations), iterations, $"Iterations must be at least {MinimalIterationCount}.");

			using var derive = new Rfc2898DeriveBytes(passphrase, salt, iterations, HashAlgorithmName.SHA256);
			return derive.GetBytes(KeySize);
		}

		[Flags]
		public enum Options
		{
			ClearPooledArrays = 1 << 0,
		}
	}
}
