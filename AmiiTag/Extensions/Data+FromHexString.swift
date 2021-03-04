//
//  Data+FromHex.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 10/4/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation

extension Data {
    static func FromHexString(hex: String) -> Data {
        var hex = hex
        var data = Data()
        while(hex.count > 0) {
            let c: String = hex.substring(to: hex.index(hex.startIndex, offsetBy: 2))
            hex = hex.substring(from: hex.index(hex.startIndex, offsetBy: 2))
            var ch: UInt32 = 0
            Scanner(string: c).scanHexInt32(&ch)
            var char = UInt8(ch)
            data.append(&char, count: 1)
        }
        return data
    }
}
