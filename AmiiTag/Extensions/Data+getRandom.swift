//
//  Data+GetRandom.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 5/16/22.
//  Copyright © 2022 Daniel Radtke. All rights reserved.
//

import Foundation

extension Data {
    static func getRandom(count: Int) -> Data {
        var bytes = [Int8](repeating: 0, count: count)

        // Fill bytes with secure random data
        let status = SecRandomCopyBytes(
            kSecRandomDefault,
            count,
            &bytes
        )
        
        // A status of errSecSuccess indicates success
        if status == errSecSuccess {
            // Convert bytes to Data
            let data = Data(bytes: bytes, count: count)
            return data
        }
        else {
            return Data(count: count)
        }
    }
}
