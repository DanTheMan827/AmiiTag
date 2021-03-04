//
//  Data+ToHex.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/3/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

extension Data {
    func ToHexString() -> String {
        return self.map { String(format: "%02hhx", $0) }.joined()
    }
}
