//
//  URL+sha256.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/27/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

extension URL {
    public func sha256() -> Data? {
        guard self.isFileURL else {
            return nil
        }
        
        do {
            let bufferSize = 1024 * 1024
            // Open file for reading:
            let file = try FileHandle(forReadingFrom: self)
            defer {
                file.closeFile()
            }

            // Create and initialize SHA256 context:
            var context = CC_SHA256_CTX()
            CC_SHA256_Init(&context)

            // Read up to `bufferSize` bytes, until EOF is reached, and update SHA256 context:
            while autoreleasepool(invoking: {
                // Read up to `bufferSize` bytes
                let data = file.readData(ofLength: bufferSize)
                if data.count > 0 {
                    _ = data.withUnsafeBytes { bytesFromBuffer -> Int32 in
                      guard let rawBytes = bytesFromBuffer.bindMemory(to: UInt8.self).baseAddress else {
                        return Int32(kCCMemoryFailure)
                      }

                      return CC_SHA256_Update(&context, rawBytes, numericCast(data.count))
                    }
                    // Continue
                    return true
                } else {
                    // End of file
                    return false
                }
            }) { }

            // Compute the SHA256 digest:
            var digestData = Data(count: Int(CC_SHA256_DIGEST_LENGTH))
            _ = digestData.withUnsafeMutableBytes { bytesFromDigest -> Int32 in
              guard let rawBytes = bytesFromDigest.bindMemory(to: UInt8.self).baseAddress else {
                return Int32(kCCMemoryFailure)
              }

              return CC_SHA256_Final(rawBytes, &context)
            }

            return digestData
        } catch {
            print(error)
            return nil
        }
    }
}
