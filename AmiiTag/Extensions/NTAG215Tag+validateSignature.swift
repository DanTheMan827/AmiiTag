//
//  NTAG215Tag+validateSignature.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 5/18/22.
//  Copyright © 2022 Daniel Radtke. All rights reserved.
//

import Foundation

extension NTAG215Tag {
    fileprivate static let pubKey = Data([0x04, 0x49, 0x4E, 0x1A, 0x38, 0x6D, 0x3D, 0x3C,
                                             0xFE, 0x3D, 0xC1, 0x0E, 0x5D, 0xE6, 0x8A, 0x49,
                                             0x9B, 0x1C, 0x20, 0x2D, 0xB5, 0xB1, 0x32, 0x39,
                                             0x3E, 0x89, 0xED, 0x19, 0xFE, 0x5B, 0xE8, 0xBC, 0x61])
    
    static func validateOriginality(uid: Data, signature sigData: Data) -> Bool {
        guard (uid.count == 7 || uid.count == 9) else {
            return false
        }
        
        var data = Data(count: 16)
        switch uid.count {
            case 7:
                data[9] = uid[0]
                data[10] = uid[1]
                data[11] = uid[2]
                data[12] = uid[3]
                data[13] = uid[4]
                data[14] = uid[5]
                data[15] = uid[6]
                break
            case 9:
                data[9] = uid[0]
                data[10] = uid[1]
                data[11] = uid[2]
                data[12] = uid[4]
                data[13] = uid[5]
                data[14] = uid[6]
                data[15] = uid[7]
                break
            default:
                return false
        }
        
        var returnValue = false
        
        Data(data).withUnsafeBytes { uid in
            Data(sigData).withUnsafeBytes { signature in
                Data(pubKey).withUnsafeBytes { pubKey in
                    returnValue = ecdsa_verify(pubKey, uid, signature) != 0
                }
            }
        }
        
        return returnValue
    }
    
    func validateOriginality() -> Bool {
        guard let signature = self.dump.signature else {
            return false
        }
        
        return NTAG215Tag.validateOriginality(uid: self.dump.uid, signature: signature)
    }
}
